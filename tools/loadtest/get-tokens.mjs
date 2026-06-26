// Харвестер access-токенов для нагрузочного теста: для пула тест-юзеров проходит OIDC
// (authorization code + PKCE) против IdP и сохраняет access_token'ы в JSON (массив строк).
// Требует Node 18+ (глобальный fetch). Для self-signed (staging/local): NODE_TLS_REJECT_UNAUTHORIZED=0.
//
// Пример:
//   IDP=https://auth.staging.example.com \
//   REDIRECT=https://app.staging.example.com/signin-oidc \
//   CLIENT_ID=chessschool-web COUNT=200 OUT=./tokens.json node get-tokens.mjs
//
// ВНИМАНИЕ: только для изолированного staging/нагрузочного контура. Не запускать против боевого IdP с
// реальными пользователями. REDIRECT обязан точно совпадать с seeded redirect_uri клиента.

import crypto from 'node:crypto';
import { writeFileSync } from 'node:fs';

const IDP = required('IDP');                                   // базовый URL IdP
const REDIRECT = required('REDIRECT');                         // {web base}/signin-oidc
const CLIENT_ID = process.env.CLIENT_ID || 'chessschool-web';
const COUNT = parseInt(process.env.COUNT || '50', 10);
const OUT = process.env.OUT || './tokens.json';
const SCOPE = 'openid profile email chess.api offline_access';

function required(name) {
  const v = process.env[name];
  if (!v) { console.error(`Не задан ${name}`); process.exit(1); }
  return v;
}
const b64url = (buf) => buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

async function harvestOne(i) {
  const jar = new Map();
  const absorb = (res) => {
    for (const c of res.headers.getSetCookie?.() ?? []) {
      const kv = c.split(';')[0]; const idx = kv.indexOf('=');
      if (idx > 0) jar.set(kv.slice(0, idx), kv.slice(idx + 1));
    }
  };
  const cookie = () => [...jar].map(([k, v]) => `${k}=${v}`).join('; ');

  const email = `load${i}@loadtest.local`, password = 'Load!12345', name = `Load ${i}`;

  // 1) Регистрация тест-юзера (если уже есть — не страшно).
  await fetch(`${IDP}/account/register`, {
    method: 'POST', redirect: 'manual',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ email, password, name, return: '/' }),
  }).then(absorb).catch(() => {});

  // 2) PKCE + запрос авторизации.
  const verifier = b64url(crypto.randomBytes(32));
  const challenge = b64url(crypto.createHash('sha256').update(verifier).digest());
  const authUrl = `${IDP}/connect/authorize?` + new URLSearchParams({
    client_id: CLIENT_ID, redirect_uri: REDIRECT, response_type: 'code', scope: SCOPE,
    code_challenge: challenge, code_challenge_method: 'S256', state: b64url(crypto.randomBytes(8)),
  });

  let res = await fetch(authUrl, { redirect: 'manual', headers: { cookie: cookie() } });
  absorb(res);
  let loc = res.headers.get('location');

  // 3) Если редирект на форму логина — логинимся и повторяем authorize.
  if (res.status >= 300 && loc && loc.includes('/account/login')) {
    const ret = new URL(loc, IDP).searchParams.get('return') || authUrl;
    res = await fetch(`${IDP}/account/login`, {
      method: 'POST', redirect: 'manual',
      headers: { 'content-type': 'application/x-www-form-urlencoded', cookie: cookie() },
      body: new URLSearchParams({ email, password, return: ret }),
    });
    absorb(res);
    res = await fetch(new URL(res.headers.get('location') || authUrl, IDP), { redirect: 'manual', headers: { cookie: cookie() } });
    absorb(res);
    loc = res.headers.get('location');
  }

  if (!loc || !loc.startsWith(REDIRECT)) throw new Error(`нет редиректа с кодом (status ${res.status}, loc ${loc})`);
  const code = new URL(loc).searchParams.get('code');
  if (!code) throw new Error('нет параметра code');

  // 4) Обмен кода на токены.
  const tok = await fetch(`${IDP}/connect/token`, {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: REDIRECT, client_id: CLIENT_ID, code_verifier: verifier }),
  });
  const json = await tok.json();
  if (!json.access_token) throw new Error('нет access_token: ' + JSON.stringify(json));
  return json.access_token;
}

const tokens = [];
for (let i = 0; i < COUNT; i++) {
  try { tokens.push(await harvestOne(i)); process.stdout.write('.'); }
  catch (e) { console.error(`\nuser ${i}: ${e.message}`); }
}
writeFileSync(OUT, JSON.stringify(tokens));
console.log(`\n${tokens.length}/${COUNT} токенов сохранено в ${OUT}`);
