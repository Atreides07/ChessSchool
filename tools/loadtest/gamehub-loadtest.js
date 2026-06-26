// k6 нагрузочный тест GameServer /gamehub: полная стейт-машина партии поверх SignalR (JSON Hub Protocol).
// Каждый VU: подключается → handshake → FindMatch → играет заранее сгенерированную ЛЕГАЛЬНУЮ партию
// (60 полуходов), определяя свой ход по полю turn, и меряет задержку хода (move_rtt).
//
// Запуск (см. README.md):
//   k6 run -e HUB=wss://game.staging.example.com -e TOKENS=./tokens.json -e VUS=2000 gamehub-loadtest.js
//
// Любые два спаренные VU играют согласованную партию: ход берётся по индексу полухода (ply) из общего
// списка MOVES — белые ходят на чётных ply, чёрные на нечётных, поэтому любая пара совместима.

import ws from 'k6/ws';
import { check } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { SharedArray } from 'k6/data';

const moveRtt = new Trend('move_rtt', true);       // отправил Move → пришёл GameState с этим ходом
const matchWait = new Trend('match_wait', true);   // FindMatch → найдена пара
const movesSent = new Counter('moves_sent');
const gamesMatched = new Counter('games_matched');
const gamesFinished = new Counter('games_finished');
const errors = new Counter('errors');

// Легальная партия (UCI from/to), сгенерирована той же библиотекой (Gera.Chess), что и сервер.
const MOVES = [
  { from: 'a2', to: 'a4' }, { from: 'f7', to: 'f5' }, { from: 'a1', to: 'a3' }, { from: 'c7', to: 'c5' },
  { from: 'h2', to: 'h4' }, { from: 'e8', to: 'f7' }, { from: 'h1', to: 'h3' }, { from: 'd8', to: 'a5' },
  { from: 'e2', to: 'e4' }, { from: 'a5', to: 'd2' }, { from: 'e1', to: 'd2' }, { from: 'b8', to: 'a6' },
  { from: 'a3', to: 'a1' }, { from: 'a6', to: 'b4' }, { from: 'd1', to: 'h5' }, { from: 'f7', to: 'f6' },
  { from: 'h5', to: 'd1' }, { from: 'b4', to: 'a2' }, { from: 'd1', to: 'h5' }, { from: 'a7', to: 'a5' },
  { from: 'h5', to: 'd1' }, { from: 'f6', to: 'g6' }, { from: 'd1', to: 'h5' }, { from: 'g6', to: 'h5' },
  { from: 'f1', to: 'a6' }, { from: 'h5', to: 'h6' }, { from: 'h3', to: 'a3' }, { from: 'c5', to: 'c4' },
  { from: 'a6', to: 'c4' }, { from: 'a2', to: 'b4' }, { from: 'g2', to: 'g4' }, { from: 'b4', to: 'a2' },
  { from: 'e4', to: 'f5' }, { from: 'a8', to: 'a6' }, { from: 'g4', to: 'g5' }, { from: 'h6', to: 'h5' },
  { from: 'g1', to: 'f3' }, { from: 'a6', to: 'h6' }, { from: 'a3', to: 'a2' }, { from: 'h6', to: 'a6' },
  { from: 'd2', to: 'e3' }, { from: 'a6', to: 'h6' }, { from: 'e3', to: 'f4' }, { from: 'h6', to: 'a6' },
  { from: 'b1', to: 'a3' }, { from: 'a6', to: 'h6' }, { from: 'f4', to: 'g3' }, { from: 'h6', to: 'a6' },
  { from: 'g3', to: 'h3' }, { from: 'a6', to: 'h6' }, { from: 'h3', to: 'h2' }, { from: 'h6', to: 'a6' },
  { from: 'f3', to: 'e1' }, { from: 'a6', to: 'h6' }, { from: 'a3', to: 'b1' }, { from: 'h6', to: 'a6' },
  { from: 'c1', to: 'f4' }, { from: 'a6', to: 'h6' }, { from: 'e1', to: 'd3' }, { from: 'h6', to: 'a6' },
];

const TOKENS = new SharedArray('tokens', () => JSON.parse(open(__ENV.TOKENS || './tokens.json')));

const HUB = __ENV.HUB || 'wss://localhost:7000';            // ws(s)://host (без /gamehub)
const INITIAL = parseInt(__ENV.INITIAL || '180', 10);
const INCREMENT = parseInt(__ENV.INCREMENT || '2', 10);
const THINK_MS = parseInt(__ENV.THINK_MS || '4000', 10);    // среднее «время на ход»
const GAME_MS = parseInt(__ENV.GAME_MS || '360000', 10);
const VUS = parseInt(__ENV.VUS || '100', 10);
const REC = String.fromCharCode(0x1e);                      // разделитель SignalR-сообщений

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-vus', startVUs: 0,
      stages: [
        { duration: __ENV.RAMP || '2m', target: VUS },
        { duration: __ENV.HOLD || '5m', target: VUS },
        { duration: __ENV.DOWN || '30s', target: 0 },
      ],
    },
  },
  insecureSkipTLSVerify: true,   // staging с self-signed; в проде убрать
  thresholds: {
    move_rtt: ['p(95)<250', 'p(99)<500'],   // SLO задержки хода (мс) — корректируйте
    errors: ['count<1'],
  },
};

const frame = (obj) => JSON.stringify(obj) + REC;

export default function () {
  const token = TOKENS[(__VU - 1) % TOKENS.length];
  const url = `${HUB}/gamehub?access_token=${token}`;

  let handshakeDone = false, gameId = null, myColor = null, invId = 1;
  let matchSentAt = 0, lastMoveSentAt = 0, finished = false;

  const res = ws.connect(url, {}, (socket) => {
    socket.on('open', () => socket.send(frame({ protocol: 'json', version: 1 })));
    socket.setInterval(() => { if (handshakeDone) socket.send(frame({ type: 6 })); }, 15000); // keep-alive ping
    socket.setTimeout(() => socket.close(), GAME_MS);

    socket.on('message', (data) => {
      for (const part of String(data).split(REC)) {
        if (!part) continue;
        let msg; try { msg = JSON.parse(part); } catch (_) { continue; }

        if (!handshakeDone) {                 // первый ответ {} — завершение handshake
          handshakeDone = true;
          matchSentAt = Date.now();
          socket.send(frame({ type: 1, invocationId: String(invId++), target: 'FindMatch', arguments: [INITIAL, INCREMENT] }));
          continue;
        }
        if (msg.type === 6) continue;         // ping

        if (msg.type === 3) {                 // completion (FindMatch / Move)
          const r = msg.result;
          if (r && r.gameId !== undefined && r.color !== undefined) {
            gameId = r.gameId; myColor = r.color;
            matchWait.add(Date.now() - matchSentAt); gamesMatched.add(1);
            if (myColor === 0) scheduleMove(socket, 0);   // белые делают ход 0
          } else if (r && r.accepted === false) {
            errors.add(1);
          }
          continue;
        }

        if (msg.type === 1 && msg.target === 'GameState') {  // broadcast состояния
          const st = msg.arguments[0];
          if (lastMoveSentAt && (st.lastFrom || st.lastTo)) { moveRtt.add(Date.now() - lastMoveSentAt); lastMoveSentAt = 0; }
          if (st.result !== undefined && st.result !== 0 /*Ongoing*/) { // партия завершена
            if (!finished) { finished = true; gamesFinished.add(1); socket.close(); }
            continue;
          }
          if (st.turn === myColor) scheduleMove(socket, plyNext(st));
        }
      }
    });
  });

  check(res, { 'ws connected (101)': (r) => r && r.status === 101 });

  function plyNext(st) { return (st.moveNumber - 1) * 2 + (st.turn === 1 ? 1 : 0); }

  function scheduleMove(socket, ply) {
    if (ply >= MOVES.length) { if (!finished) { finished = true; socket.close(); } return; }
    const delay = THINK_MS * (0.6 + Math.random() * 0.8);  // джиттер темпа
    socket.setTimeout(() => {
      const m = MOVES[ply];
      lastMoveSentAt = Date.now();
      socket.send(frame({ type: 1, invocationId: String(invId++), target: 'Move', arguments: [gameId, m.from, m.to, null] }));
      movesSent.add(1);
    }, delay);
  }
}
