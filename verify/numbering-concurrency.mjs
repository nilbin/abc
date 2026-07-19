// Durable order numbering under concurrency (Sol re-review, Finding 5B): fire N orders.create
// in parallel and assert every committed number is UNIQUE. On Postgres the counter row's write
// lock (held to commit) serializes the creators; the point of this probe is to prove no two
// concurrent creators ever receive the same number. NOT idempotent — run on a FRESH database,
// and run it on Postgres (SQLite serializes writes anyway, so it's the weaker check).
import crypto from 'node:crypto';
const BASE = 'http://localhost:5100';
const b64url = (b) => b.toString('base64').replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
async function pkce(email, tenant) {
  const verifier = b64url(crypto.randomBytes(32));
  const challenge = b64url(crypto.createHash('sha256').update(verifier).digest());
  const jar = new Map(); const ck = () => [...jar.values()].join('; ');
  const sc = (r) => { for (const c of r.headers.getSetCookie?.() ?? []) { const p = c.split(';')[0]; jar.set(p.split('=')[0], p); } };
  const au = `${BASE}/connect/authorize?client_id=tam-spa&response_type=code&redirect_uri=${encodeURIComponent('http://localhost:5173/callback')}&code_challenge=${challenge}&code_challenge_method=S256&tenant=${tenant}`;
  let r = await fetch(au, {redirect:'manual'}); sc(r);
  r = await fetch(`${BASE}/connect/authorize/login`, {method:'POST',redirect:'manual',
    headers:{'content-type':'application/x-www-form-urlencoded',cookie:ck()},
    body:new URLSearchParams({email,password:'demo123',returnUrl:au.slice(BASE.length)})}); sc(r);
  let next = r.headers.get('location'), code = null;
  for (let i=0;i<6&&next&&!code;i++){const u = next.startsWith('http')?next:BASE+next;
    if (u.startsWith('http://localhost:5173/callback')){code=new URL(u).searchParams.get('code');break;}
    r = await fetch(u,{redirect:'manual',headers:{cookie:ck()}}); sc(r); next=r.headers.get('location');}
  const tr = await fetch(`${BASE}/connect/token`,{method:'POST',headers:{'content-type':'application/x-www-form-urlencoded'},
    body:new URLSearchParams({grant_type:'authorization_code',client_id:'tam-spa',code,redirect_uri:'http://localhost:5173/callback',code_verifier:verifier})});
  return (await tr.json()).access_token;
}
const op = (t, id, body) => fetch(`${BASE}/api/operations/${id}`, {method:'POST',
  headers:{authorization:`Bearer ${t}`,'content-type':'application/json'},
  body: JSON.stringify(body)}).then(async r=>({status:r.status, body:await r.json()}));
const view = (t, id, q='') => fetch(`${BASE}/api/views/${id}${q}`,
  {headers:{authorization:`Bearer ${t}`}}).then(r=>r.json());

const results = [];
const check = (n, ok, d) => { results.push(ok); console.log(`${ok?'PASS':'FAIL'}  ${n}  ${d??''}`); };

const alva = await pkce('alva','demo');
const customerId = (await view(alva,'customers.list','?pageSize=5')).body?.rows?.[0]?.id
  ?? (await view(alva,'customers.list','?pageSize=5')).rows?.[0]?.id;
check('got a seed customer', !!customerId, customerId ?? 'none');

const N = 25;
const responses = await Promise.all(Array.from({length: N}, (_, i) =>
  op(alva, 'orders.create', {
    customerId, orderType: 'service', workAddress: `Parallellgatan ${i}`,
    description: `concurrency probe #${i}`,
  })));

const ok = responses.filter(r => r.status === 200);
check(`all ${N} concurrent creates succeeded`, ok.length === N, `succeeded=${ok.length}/${N}`);

const numbers = ok.map(r => r.body?.output?.number).filter(Boolean);
const unique = new Set(numbers);
check('every order number is unique', unique.size === numbers.length,
  `numbers=${numbers.length} unique=${unique.size}`);
check('numbers form a contiguous run (no gaps, no reuse)',
  (() => {
    const nums = numbers.map(s => Number(s.split('-')[1])).sort((a,b)=>a-b);
    return nums.length > 0 && nums.every((v,i) => i === 0 || v === nums[i-1] + 1);
  })(), numbers.slice().sort().join(','));

console.log(`\n${results.filter(Boolean).length}/${results.length} passed`);
process.exit(results.every(Boolean) ? 0 : 1);
