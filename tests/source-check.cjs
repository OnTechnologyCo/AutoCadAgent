const fs=require('fs'), path=require('path'), assert=require('assert');
const root=path.resolve(__dirname,'..'), cad=path.join(root,'src','AutoCadAgent.AutoCAD');
const files=['ToolRegistry.cs',...fs.readdirSync(path.join(cad,'Tools')).filter(f=>f.endsWith('.cs')).map(f=>'Tools/'+f)];
const names=[], staged=[];
for(const file of files){
 const source=fs.readFileSync(path.join(cad,file),'utf8');
 const classes=[...source.matchAll(/public (?:sealed )?class (\w+)\s*:\s*ChatTool\s*\{/g)];
 for(let i=0;i<classes.length;i++){
  const body=source.slice(classes[i].index,classes[i+1]?.index??source.length), name=body.match(/Name => "([^"]+)"/)?.[1];
  assert(name,`Missing literal name: ${classes[i][1]}`);names.push(name);
  const parameters=[...body.matchAll(/new ToolParam\s*\{\s*Name\s*=\s*"([^"]+)"\s*,\s*Type\s*=\s*"([^"]+)"/g)];
  const keys=parameters.map(m=>m[1]);assert.equal(new Set(keys).size,keys.length,`Duplicate parameters: ${name}`);
  for(const p of parameters)assert(['number','string','array','boolean','object'].includes(p[2]),`Invalid type: ${name}.${p[1]}`);
  for(const used of body.matchAll(/(?:TryGet\w+|GetString|GetBool|GetHandleArray|AsDouble|AsString)\(args,\s*"([^"]+)"/g))assert(keys.includes(used[1]),`Undeclared argument: ${name}.${used[1]}`);
  if(body.includes('DrawSession.Add'))staged.push(name);
 }
}
assert.equal(names.length,new Set(names).size,'Duplicate tool name');
const catalog=JSON.parse(fs.readFileSync(path.join(root,'docs','tool-catalog.json'),'utf8'));
assert.deepEqual(names.sort(),Object.values(catalog.groups).flat().sort(),'Source and documented catalog differ');
const contract=fs.readFileSync(path.join(cad,'ToolContract.cs'),'utf8').split('private static readonly HashSet<string> Reads')[0];
assert.deepEqual(staged.sort(),[...contract.matchAll(/"(draw\.[^"]+)"/g)].map(m=>m[1]).sort(),'Staged metadata differs from actual buffer writes');
console.log(`PASS source contracts: ${names.length} names, parameter declarations and ${staged.length} staged implementations`);
