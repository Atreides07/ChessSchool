// k6 нагрузочный тест GameServer /gamehub: полная стейт-машина партии поверх SignalR (JSON Hub Protocol).
// Каждый VU: подключается → handshake → FindMatch → играет легальную партию, определяя свой ход по полю
// turn, и меряет задержку хода (move_rtt).
//
// Реализм:
//  - Распределение контролей времени (bullet/blitz/rapid) — VU выбирает по весам; темп хода под контроль.
//  - Несколько дебютов: дебют выбирается ДЕТЕРМИНИРОВАННО по gameId, поэтому оба игрока партии (которые
//    знают только общий gameId) играют одну и ту же легальную линию без рассинхрона.
//
// Запуск (см. README.md):
//   k6 run -e HUB=wss://game.staging.example.com -e TOKENS=./tokens.json -e VUS=2000 gamehub-loadtest.js

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

// Несколько легальных партий (UCI from/to), сгенерированы той же библиотекой (Gera.Chess), что и сервер.
const OPENINGS = [
  [
    { from: 'a2', to: 'a4' }, { from: 'h7', to: 'h5' }, { from: 'g1', to: 'f3' }, { from: 'g8', to: 'h6' },
    { from: 'e2', to: 'e3' }, { from: 'd7', to: 'd5' }, { from: 'g2', to: 'g3' }, { from: 'd8', to: 'd7' },
    { from: 'd2', to: 'd3' }, { from: 'f7', to: 'f6' }, { from: 'c2', to: 'c4' }, { from: 'b7', to: 'b6' },
    { from: 'b1', to: 'c3' }, { from: 'd5', to: 'd4' }, { from: 'b2', to: 'b3' }, { from: 'g7', to: 'g6' },
    { from: 'c3', to: 'b5' }, { from: 'g6', to: 'g5' }, { from: 'e3', to: 'd4' }, { from: 'g5', to: 'g4' },
    { from: 'f3', to: 'g5' }, { from: 'e7', to: 'e6' }, { from: 'h1', to: 'g1' }, { from: 'e8', to: 'e7' },
    { from: 'b5', to: 'd6' }, { from: 'a7', to: 'a6' }, { from: 'f2', to: 'f3' }, { from: 'a6', to: 'a5' },
    { from: 'd4', to: 'd5' }, { from: 'f8', to: 'g7' }, { from: 'f1', to: 'e2' }, { from: 'b8', to: 'a6' },
    { from: 'a1', to: 'b1' }, { from: 'a6', to: 'b4' }, { from: 'e1', to: 'f2' }, { from: 'h8', to: 'g8' },
    { from: 'f2', to: 'e1' }, { from: 'a8', to: 'b8' }, { from: 'g5', to: 'f7' }, { from: 'g7', to: 'f8' },
    { from: 'd6', to: 'b5' }, { from: 'd7', to: 'c6' }, { from: 'e1', to: 'd2' }, { from: 'g8', to: 'g5' },
    { from: 'f3', to: 'g4' }, { from: 'b4', to: 'd3' }, { from: 'd1', to: 'f1' }, { from: 'g5', to: 'g8' },
    { from: 'b5', to: 'c7' }, { from: 'g8', to: 'g4' }, { from: 'f7', to: 'd6' }, { from: 'b8', to: 'a8' },
    { from: 'd6', to: 'b5' }, { from: 'd3', to: 'e5' }, { from: 'b5', to: 'c3' }, { from: 'e5', to: 'f3' },
    { from: 'd2', to: 'c2' }, { from: 'g4', to: 'h4' }, { from: 'c7', to: 'e8' }, { from: 'c6', to: 'c5' },
  ],
  [
    { from: 'd2', to: 'd3' }, { from: 'c7', to: 'c5' }, { from: 'h2', to: 'h3' }, { from: 'f7', to: 'f5' },
    { from: 'e1', to: 'd2' }, { from: 'h7', to: 'h5' }, { from: 'b2', to: 'b3' }, { from: 'd8', to: 'a5' },
    { from: 'b1', to: 'c3' }, { from: 'a5', to: 'b6' }, { from: 'c1', to: 'b2' }, { from: 'g7', to: 'g6' },
    { from: 'b2', to: 'a3' }, { from: 'b6', to: 'd8' }, { from: 'a1', to: 'b1' }, { from: 'a7', to: 'a6' },
    { from: 'f2', to: 'f3' }, { from: 'h8', to: 'h7' }, { from: 'a3', to: 'b2' }, { from: 'd7', to: 'd6' },
    { from: 'd3', to: 'd4' }, { from: 'h5', to: 'h4' }, { from: 'd4', to: 'd5' }, { from: 'e8', to: 'd7' },
    { from: 'd2', to: 'e1' }, { from: 'g8', to: 'h6' }, { from: 'd1', to: 'd4' }, { from: 'h6', to: 'g4' },
    { from: 'd4', to: 'c4' }, { from: 'g4', to: 'e5' }, { from: 'b2', to: 'a3' }, { from: 'e5', to: 'c6' },
    { from: 'c4', to: 'b5' }, { from: 'g6', to: 'g5' }, { from: 'b5', to: 'c6' }, { from: 'b7', to: 'c6' },
    { from: 'e1', to: 'd2' }, { from: 'd7', to: 'c7' }, { from: 'd2', to: 'c1' }, { from: 'a8', to: 'a7' },
    { from: 'c1', to: 'd1' }, { from: 'h7', to: 'h6' }, { from: 'b1', to: 'a1' }, { from: 'h6', to: 'h8' },
    { from: 'b3', to: 'b4' }, { from: 'e7', to: 'e5' }, { from: 'b4', to: 'b5' }, { from: 'd8', to: 'e8' },
    { from: 'd1', to: 'c1' }, { from: 'f5', to: 'f4' }, { from: 'a3', to: 'b2' }, { from: 'c8', to: 'd7' },
    { from: 'c3', to: 'd1' }, { from: 'e8', to: 'd8' }, { from: 'c1', to: 'b1' }, { from: 'a6', to: 'a5' },
    { from: 'g2', to: 'g3' }, { from: 'f4', to: 'g3' }, { from: 'c2', to: 'c4' }, { from: 'd7', to: 'f5' },
  ],
  [
    { from: 'd2', to: 'd3' }, { from: 'd7', to: 'd5' }, { from: 'c2', to: 'c4' }, { from: 'h7', to: 'h5' },
    { from: 'g2', to: 'g4' }, { from: 'f7', to: 'f5' }, { from: 'c4', to: 'd5' }, { from: 'f5', to: 'g4' },
    { from: 'b1', to: 'c3' }, { from: 'b8', to: 'a6' }, { from: 'f2', to: 'f3' }, { from: 'g8', to: 'h6' },
    { from: 'b2', to: 'b4' }, { from: 'e8', to: 'f7' }, { from: 'c3', to: 'b5' }, { from: 'h6', to: 'g8' },
    { from: 'f3', to: 'f4' }, { from: 'c7', to: 'c6' }, { from: 'e1', to: 'd2' }, { from: 'e7', to: 'e6' },
    { from: 'f1', to: 'h3' }, { from: 'c6', to: 'd5' }, { from: 'b5', to: 'd6' }, { from: 'f7', to: 'f6' },
    { from: 'd6', to: 'f5' }, { from: 'f8', to: 'c5' }, { from: 'f5', to: 'g7' }, { from: 'b7', to: 'b5' },
    { from: 'd2', to: 'c2' }, { from: 'c5', to: 'f8' }, { from: 'c2', to: 'b2' }, { from: 'f8', to: 'b4' },
    { from: 'h3', to: 'f1' }, { from: 'd8', to: 'b6' }, { from: 'c1', to: 'e3' }, { from: 'b4', to: 'a5' },
    { from: 'a2', to: 'a3' }, { from: 'b6', to: 'd6' }, { from: 'e3', to: 'f2' }, { from: 'a5', to: 'e1' },
    { from: 'a1', to: 'c1' }, { from: 'f6', to: 'e7' }, { from: 'f1', to: 'g2' }, { from: 'a6', to: 'c7' },
    { from: 'g1', to: 'h3' }, { from: 'h8', to: 'h7' }, { from: 'h1', to: 'f1' }, { from: 'd6', to: 'd8' },
    { from: 'g2', to: 'h1' }, { from: 'd8', to: 'e8' }, { from: 'b2', to: 'c2' }, { from: 'a7', to: 'a5' },
    { from: 'f2', to: 'c5' }, { from: 'e7', to: 'd8' }, { from: 'c5', to: 'a7' }, { from: 'c8', to: 'd7' },
    { from: 'h3', to: 'f2' }, { from: 'd5', to: 'd4' }, { from: 'f2', to: 'e4' }, { from: 'e1', to: 'b4' },
  ],
  [
    { from: 'c2', to: 'c3' }, { from: 'b7', to: 'b5' }, { from: 'a2', to: 'a3' }, { from: 'c7', to: 'c5' },
    { from: 'f2', to: 'f4' }, { from: 'f7', to: 'f6' }, { from: 'd1', to: 'b3' }, { from: 'c8', to: 'a6' },
    { from: 'g2', to: 'g3' }, { from: 'd8', to: 'a5' }, { from: 'g3', to: 'g4' }, { from: 'e7', to: 'e6' },
    { from: 'b3', to: 'd5' }, { from: 'd7', to: 'd6' }, { from: 'd5', to: 'c4' }, { from: 'a6', to: 'b7' },
    { from: 'c4', to: 'b3' }, { from: 'a7', to: 'a6' }, { from: 'f1', to: 'h3' }, { from: 'c5', to: 'c4' },
    { from: 'b3', to: 'c4' }, { from: 'b5', to: 'c4' }, { from: 'g4', to: 'g5' }, { from: 'a5', to: 'f5' },
    { from: 'e1', to: 'd1' }, { from: 'g8', to: 'e7' }, { from: 'g5', to: 'g6' }, { from: 'f5', to: 'g4' },
    { from: 'b2', to: 'b4' }, { from: 'h8', to: 'g8' }, { from: 'b4', to: 'b5' }, { from: 'e8', to: 'd8' },
    { from: 'h3', to: 'g2' }, { from: 'b7', to: 'g2' }, { from: 'd2', to: 'd4' }, { from: 'h7', to: 'g6' },
    { from: 'd1', to: 'd2' }, { from: 'e7', to: 'f5' }, { from: 'a3', to: 'a4' }, { from: 'd8', to: 'd7' },
    { from: 'b5', to: 'a6' }, { from: 'g2', to: 'h1' }, { from: 'e2', to: 'e3' }, { from: 'd7', to: 'e7' },
    { from: 'g1', to: 'h3' }, { from: 'b8', to: 'd7' }, { from: 'a1', to: 'a2' }, { from: 'a8', to: 'c8' },
    { from: 'a2', to: 'b2' }, { from: 'g6', to: 'g5' }, { from: 'h3', to: 'g5' }, { from: 'f5', to: 'g3' },
    { from: 'b2', to: 'b4' }, { from: 'd7', to: 'c5' }, { from: 'f4', to: 'f5' }, { from: 'g4', to: 'd1' },
    { from: 'd2', to: 'd1' }, { from: 'c8', to: 'd8' }, { from: 'c1', to: 'd2' }, { from: 'c5', to: 'a4' },
  ],
];

// Распределение контролей времени и темпа хода (доли — под реальный микс трафика; меняйте).
const TC_MIX = [
  { initial: 60, increment: 0, weight: 0.30, thinkMs: 1500 },  // bullet
  { initial: 180, increment: 2, weight: 0.50, thinkMs: 4000 }, // blitz
  { initial: 600, increment: 5, weight: 0.20, thinkMs: 8000 }, // rapid
];

// Принудительно один контроль времени: -e FORCE_TC="180,2,4000" (initial,increment,thinkMs).
// Удобно для детерминированного матчмейкинга (все в одном пуле) и точечных прогонов.
const TC_POOL = (() => {
  if (!__ENV.FORCE_TC) return TC_MIX;
  const [i, inc, t] = __ENV.FORCE_TC.split(',').map(Number);
  return [{ initial: i, increment: inc, weight: 1, thinkMs: t || 4000 }];
})();

const TOKENS = new SharedArray('tokens', () => JSON.parse(open(__ENV.TOKENS || './tokens.json')));

const HUB = __ENV.HUB || 'wss://localhost:7000';            // ws(s)://host (без /gamehub)
const THINK_SCALE = parseFloat(__ENV.THINK_SCALE || '1');   // множитель темпа (ускорить/замедлить тест)
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
function pickTc() {
  let r = Math.random(), acc = 0;
  let total = 0; for (const tc of TC_POOL) total += tc.weight;
  r *= total;
  for (const tc of TC_POOL) { acc += tc.weight; if (r <= acc) return tc; }
  return TC_POOL[TC_POOL.length - 1];
}
function openingFor(gameId) {            // детерминированно по gameId → оба игрока играют одну линию
  let h = 0;
  for (let i = 0; i < gameId.length; i++) h = (h * 31 + gameId.charCodeAt(i)) | 0;
  return OPENINGS[Math.abs(h) % OPENINGS.length];
}

export default function () {
  const token = TOKENS[(__VU - 1) % TOKENS.length];
  const url = `${HUB}/gamehub?access_token=${token}`;
  const tc = pickTc();
  const thinkMs = tc.thinkMs * THINK_SCALE;

  let handshakeDone = false, gameId = null, myColor = null, invId = 1;
  let matchSentAt = 0, lastMoveSentAt = 0, finished = false, moves = null;

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
          socket.send(frame({ type: 1, invocationId: String(invId++), target: 'FindMatch', arguments: [tc.initial, tc.increment] }));
          continue;
        }
        if (msg.type === 6) continue;         // ping

        if (msg.type === 3) {                 // completion (FindMatch / Move)
          const r = msg.result;
          if (r && r.gameId !== undefined && r.color !== undefined) {
            gameId = r.gameId; myColor = r.color; moves = openingFor(gameId);
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
    if (!moves || ply >= moves.length) { if (!finished) { finished = true; socket.close(); } return; }
    const delay = thinkMs * (0.6 + Math.random() * 0.8);  // джиттер темпа
    socket.setTimeout(() => {
      const m = moves[ply];
      lastMoveSentAt = Date.now();
      socket.send(frame({ type: 1, invocationId: String(invId++), target: 'Move', arguments: [gameId, m.from, m.to, null] }));
      movesSent.add(1);
    }, delay);
  }
}
