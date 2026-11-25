const fs = require('fs');
const path = require('path');
const statusFile = path.resolve('C:/Taosif/Project/querithm/.git_modified_files.txt');
const content = fs.readFileSync(statusFile, 'utf8').trim();
const entries = content.split(/\s+/);
const files = [];
for (let i = 0; i < entries.length; i++) {
  const token = entries[i];
  if (token === 'M' || token === '??') {
    const file = entries[i+1];
    if (file) files.push(file);
    i++;
  }
}
function report(file, line, col, severity, category, message, snippet) {
  return {file, line, col, severity, category, message, snippet};
}
const findings = [];
files.forEach(fp => {
  const absPath = path.resolve('C:/Taosif/Project/querithm', fp);
  if (!fs.existsSync(absPath)) return;
  const data = fs.readFileSync(absPath, 'utf8');
  const lines = data.split(/\r?\n/);
  lines.forEach((ln, idx) => {
    const lineNum = idx+1;
    if (/\bconsole\.(log|debug|info|warn|error)\b/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('console'), '🟡', 'DebugStatement', 'Potential leftover console statement', ln.trim()));
    }
    if (/\bTODO\b|\bFIXME\b/.test(ln)) {
      findings.push(report(fp, lineNum, ln.search(/TODO|FIXME/), '🟢', 'CommentTag', 'TODO/FIXME comment present', ln.trim()));
    }
    if (/\bany\b/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('any'), '🟡', 'AnyType', 'Usage of any type may hide bugs', ln.trim()));
    }
    if (/['\"][A-Za-z0-9_]{20,}['\"]/g.test(ln)) {
      const match = ln.match(/['\"]([A-Za-z0-9_]{20,})['\"]/);
      if (match) {
        findings.push(report(fp, lineNum, ln.indexOf(match[0]), '🔴', 'HardcodedSecret', 'Possible hardcoded secret/API key', match[0]));
      }
    }
    if (/\b(query|sql)\s*[:=]\s*`[^`]*\$\{[^}]+\}[^`]*`/.test(ln) || /\b(query|sql)\s*[:=]\s*['\"][^'\"]*\+\s*\w+/.test(ln)) {
      findings.push(report(fp, lineNum, ln.search(/\+/), '🟠', 'SQLInjectionRisk', 'Potential SQL injection via string concatenation', ln.trim()));
    }
    if (/\.then\(/.test(ln) && !/await\s+/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('.then'), '🟠', 'AsyncAwaitIssue', 'Promise .then used without await', ln.trim()));
    }
    if (/catch\s*\(.*\)\s*\{\s*\}/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('catch'), '🟠', 'EmptyCatch', 'Empty catch block may swallow errors', ln.trim()));
    }
  });
});
console.log(JSON.stringify(findings, null, 2));
