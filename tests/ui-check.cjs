process.env.PLAYWRIGHT_BROWSERS_PATH=require('path').resolve(__dirname,'../.tools/browsers');
const {chromium}=require('../.tools/node_modules/playwright-core');
const fs=require('fs');const path=require('path');const root=path.resolve(__dirname,'..');const out=path.join(root,'artifacts','validation');fs.mkdirSync(out,{recursive:true});
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:chromium.executablePath(),args:['--no-sandbox']});
 const page=await browser.newPage({viewport:{width:1240,height:850}});let errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{window.messages=[];window.handlers=[];window.chrome={webview:{postMessage:m=>messages.push(m),addEventListener:(n,cb)=>handlers.push(cb)}};window.inject=m=>handlers.forEach(fn=>fn({data:m}));});
 await page.goto(require('url').pathToFileURL(path.join(root,'web','index.html')).href);
 await page.evaluate(()=>{
  inject({t:'settings',workspace:'C:\\CADWorkspace',providers:[{id:'gpt',label:'GPT · Codex',path:'',model:''},{id:'claude',label:'Claude Code',path:'',model:''},{id:'gemini',label:'Gemini CLI',path:'',model:''}]});
  inject({t:'bridges',items:[]});
 });
 await page.screenshot({path:path.join(out,'ui-empty.png')});
 await page.selectOption('#provider','gemini');await page.fill('#model','gemini-test');await page.click('#start');
 const last=await page.evaluate(()=>messages.at(-1));if(last.id!=='gemini'||last.model!=='gemini-test')throw new Error('provider/model selection mismatch');
 await page.evaluate(()=>{inject({t:'opened',id:'gemini',label:'Gemini CLI',document:'Renderer test — mock CAD document'});});
 const text='\x1b[36mAutoCAD Agent · renderer test\x1b[0m\r\n한글 도면과 치수를 확인합니다.\r\n> ';const bytes=Buffer.from(text,'utf8');
 for(let i=0;i<bytes.length;i+=7)await page.evaluate(data=>inject({t:'output',id:'gemini',data}),bytes.subarray(i,i+7).toString('base64'));
 await page.waitForTimeout(300);
 const terminal=await page.evaluate(()=>sessions.get('gemini').term.buffer.active.getLine(1).translateToString());
 if(!terminal.includes('한글 도면'))throw new Error('UTF8 renderer failure: '+terminal);
 await page.screenshot({path:path.join(out,'ui-terminal.png')});
 await page.setViewportSize({width:860,height:580});await page.waitForTimeout(200);await page.screenshot({path:path.join(out,'ui-small.png')});
 const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);if(overflow)throw new Error('horizontal overflow');
 if(errors.length)throw new Error(errors.join('\n'));
 console.log('PASS UI load; provider/model routing; fragmented UTF-8 terminal; resize 860x580; no page errors');
 await browser.close();
})().then(()=>fs.writeFileSync(path.join(out,'ui-result.txt'),'PASS UI load, provider/model routing, fragmented UTF8 terminal, 860x580 resize, no page errors')).catch(e=>{fs.writeFileSync(path.join(out,'ui-result.txt'),'FAIL '+e.stack);process.exit(1)});

