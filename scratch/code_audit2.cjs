const fs = require('fs');
const path = require('path');
const statusFile = path.resolve('C:/Taosif/Project/querithm/.git_modified_files.txt');
const content = fs.readFileSync(statusFile,'utf8').trim();
const tokens = content.split(/\s+/);
const files = [];
for(let i=0;i<tokens.length;i++){
  const token=tokens[i];
  if(token==='M' || token==='??'){
    const file=tokens[i+1];
    if(file) files.push(file);
    i++;
  }
}
const patterns = [
  {name:'DebugStatement',regex:/\bconsole\.(log|debug|info|warn|error)\b/,severity:'🟡'},
  {name:'CommentTag',regex:/\bTODO\b|\bFIXME\b/,severity:'🟢'},
  {name:'AnyType',regex:/\bany\b/,severity:'🟡'},
  {name:'HardcodedSecret',regex:/['\"]([A-Za-z0-9_]{20,})['\"]/,severity:'🔴'},
  {name:'EvalUsage',regex:/\beval\s*\(|\bnew\s+Function\s*\(/,severity:'🟠'},
  {name:'InnerHTMLAssign',regex:/\.innerHTML\s*=/,severity:'🟠'},
  {name:'SQLInjectionRisk',regex:/\b(query|sql)\s*[:=]\s*`[^`]*\$\{[^}]+\}[^`]*`|\b(query|sql)\s*[:=]\s*['\"][^'\"]*\+\s*\w+/i,severity:'🟠'},
  {name:'AsyncAwaitIssue',regex:/\.then\(/,severity:'🟠'},
  {name:'EmptyCatch',regex:/catch\s*\(.*\)\s*\{\s*\}/,severity:'🟠'}
];
function report(file,line,col,severity,category,msg,snippet){
  return {file,line,col,severity,category,message:msg,snippet};
}
const findings=[];
files.forEach(rel=>{
  const abs=path.resolve('C:/Taosif/Project/querithm',rel);
  if(!fs.existsSync(abs)) return;
  const data=fs.readFileSync(abs,'utf8');
  const lines=data.split(/\r?\n/);
  lines.forEach((ln,idx)=>{
    const lineNum=idx+1;
    patterns.forEach(p=>{
      const match=ln.match(p.regex);
      if(match){
        const col=ln.indexOf(match[0]);
        const msg=`${p.name} detected`;
        findings.push(report(rel,lineNum,col,p.severity,p.name,msg,ln.trim()));
      }
    });
  });
});
console.log(JSON.stringify(findings,null,2));
