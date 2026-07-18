// Plugin-on-plugin (docs/37 D-V4): the L1 activation guard end to end. fortnox DependsOn
// invoicing, so it is activatable only where invoicing is active, and invoicing cannot be
// deactivated out from under an active fortnox. (The L2 contract coupling — fortnox consuming
// invoicing.invoice-finalized through the generated facade — is proven at Build(): the erp host
// composes the cross-plugin edge, which PLG010 would reject without the declared DependsOn.)
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
const op = (t,id,i) => fetch(`${BASE}/api/operations/${id}`,{method:'POST',
  headers:{authorization:`Bearer ${t}`,'content-type':'application/json'},
  body:JSON.stringify(i)}).then(async r=>({status:r.status, body:await r.json().catch(()=>null)}));
const results = [];
const check = (n, ok, d) => { results.push(ok); console.log(`${ok?'PASS':'FAIL'}  ${n}  ${d??''}`); };
const codes = (r) => (r.body?.findings ?? []).map(f => f.code);

const alva = await pkce('alva','demo');   // admin: plugins.manage

// Seed state: invoicing active, fortnox entitled but inactive. Free invoicing (nothing depends
// on it yet) so we can test the dependency guard from a clean edge.
const freeInvoicing = await op(alva, 'plugins.deactivate', { pluginId: 'invoicing' });
check('invoicing deactivates while fortnox inactive', freeInvoicing.status === 200, `status=${freeInvoicing.status}`);

// 1) fortnox refuses to activate with its parent invoicing inactive — the L1 guard.
const orphan = await op(alva, 'plugins.activate', { pluginId: 'fortnox' });
check('activating fortnox without invoicing is refused',
  codes(orphan).includes('plugins.dependency-inactive'), codes(orphan).join(','));

// 2) bring the parent up, then the child activates.
const upInvoicing = await op(alva, 'plugins.activate', { pluginId: 'invoicing' });
check('invoicing activates', upInvoicing.status === 200 && upInvoicing.body?.output?.active === true, `status=${upInvoicing.status}`);
const upFortnox = await op(alva, 'plugins.activate', { pluginId: 'fortnox' });
check('fortnox activates once invoicing is active', upFortnox.status === 200 && upFortnox.body?.output?.active === true,
  `status=${upFortnox.status} ${codes(upFortnox).join(',')}`);

// 3) invoicing can no longer be pulled out from under the active fortnox — the invariant holds.
const blockedDown = await op(alva, 'plugins.deactivate', { pluginId: 'invoicing' });
check('invoicing deactivation refused while fortnox depends on it',
  codes(blockedDown).includes('plugins.dependent-active'), codes(blockedDown).join(','));

// 4) top-down works: drop the child, then the parent frees.
const downFortnox = await op(alva, 'plugins.deactivate', { pluginId: 'fortnox' });
check('fortnox deactivates', downFortnox.status === 200 && downFortnox.body?.output?.active === false, `status=${downFortnox.status}`);
const downInvoicing = await op(alva, 'plugins.deactivate', { pluginId: 'invoicing' });
check('invoicing deactivates once fortnox is gone', downInvoicing.status === 200, `status=${downInvoicing.status}`);

// Restore the seed's active-invoicing state for any downstream reader.
await op(alva, 'plugins.activate', { pluginId: 'invoicing' });

console.log(`\n${results.filter(Boolean).length}/${results.length} passed`);
process.exit(results.every(Boolean) ? 0 : 1);
