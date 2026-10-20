import {createServer,request as proxyRequest} from 'node:http';
import {createReadStream} from 'node:fs';
import {stat} from 'node:fs/promises';
import {dirname,extname,resolve,sep} from 'node:path';
import {fileURLToPath} from 'node:url';

// Local verification only. Production serves this same build through the ASP.NET application.
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../dist/firstblick/browser');
const port=Number(process.env.FIRSTBLICK_PREVIEW_PORT||4300),api=new URL(process.env.FIRSTBLICK_PREVIEW_API||'http://127.0.0.1:5080');
if(!Number.isInteger(port)||port<1024||port>65535||api.protocol!=='http:'||!['127.0.0.1','localhost'].includes(api.hostname))throw new Error('The preview requires a local HTTP API and a valid local port.');
await stat(resolve(root,'index.html'));
const types={'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.json':'application/json','.webmanifest':'application/manifest+json','.svg':'image/svg+xml','.png':'image/png','.ico':'image/x-icon','.woff2':'font/woff2'};
const server=createServer(async(req,res)=>{
  let path;try{path=new URL(req.url||'/','http://127.0.0.1').pathname;}catch{res.writeHead(400);res.end();return;}
  if(path.startsWith('/api/')){
    const upstream=proxyRequest(new URL(req.url||'/',api),{method:req.method,headers:{...req.headers,host:api.host}},response=>{res.writeHead(response.statusCode||502,response.headers);response.pipe(res);});
    upstream.on('error',()=>{if(!res.headersSent)res.writeHead(502,{'Content-Type':'application/problem+json','Cache-Control':'no-store'});res.end(JSON.stringify({status:502,title:'Local API unavailable'}));});
    req.on('aborted',()=>upstream.destroy());req.pipe(upstream);return;
  }
  if(!['GET','HEAD'].includes(req.method||'GET')){res.writeHead(405);res.end();return;}
  let target;try{target=resolve(root,'.'+decodeURIComponent(path));}catch{res.writeHead(400);res.end();return;}
  if(target!==root&&!target.startsWith(root+sep)){res.writeHead(403);res.end();return;}
  let info;try{info=await stat(target);if(!info.isFile())throw new Error('Not a file');}
  catch{if((req.headers.accept||'').includes('text/html')){target=resolve(root,'index.html');info=await stat(target);}else{res.writeHead(404);res.end();return;}}
  res.writeHead(200,{'Content-Type':types[extname(target)]||'application/octet-stream','Content-Length':info.size,'Cache-Control':'no-cache','X-Content-Type-Options':'nosniff'});
  if(req.method==='HEAD'){res.end();return;}const stream=createReadStream(target);stream.on('error',()=>res.destroy());stream.pipe(res);
});
server.listen(port,'127.0.0.1',()=>process.stdout.write(`FirstBlick production preview: http://127.0.0.1:${port}\n`));
const close=()=>server.close(()=>process.exit(0));process.on('SIGINT',close);process.on('SIGTERM',close);
