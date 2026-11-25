const fs = require('fs');
const path = require('path');
const statusFile = path.resolve('C:/Taosif/Project/querithm/.git_modified_files.txt');
const content = fs.readFileSync(statusFile, 'utf8').trim();
// Split by spaces, each entry like 'M' or '??' followed by file path
const entries = content.split(/\s+/);
const files = [];
for (let i = 0; i < entries.length; i++) {
  const token = entries[i];
  if (token === 'M' || token === '??') {
    const file = entries[i+1];
    if (file) files.push(file);
    i++; // skip next token
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
    // Check for console.log/debug
    if (/\bconsole\.(log|debug|info|warn|error)\b/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('console'), '🟡', 'DebugStatement', 'Potential leftover console statement', ln.trim()));
    }
    // Check for TODO/FIXME
    if (/\bTODO\b|\bFIXME\b/.test(ln)) {
      findings.push(report(fp, lineNum, ln.search(/TODO|FIXME/), '🟢', 'CommentTag', 'TODO/FIXME comment present', ln.trim()));
    }
    // Check for any type in TypeScript
    if (/\bany\b/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('any'), '🟡', 'AnyType', 'Usage of any type may hide bugs', ln.trim()));
    }
    // Check for hardcoded secret patterns (simple regex for alphanumeric strings of length >=20)
    if (/['\"][A-Za-z0-9_]{20,}['\"]/g.test(ln)) {
      const match = ln.match(/['\"]([A-Za-z0-9_]{20,})['\"]/);
      if (match) {
        findings.push(report(fp, lineNum, ln.indexOf(match[0]), '🔴', 'HardcodedSecret', 'Possible hardcoded secret/API key', match[0]));
      }
    }
    // Simple SQL injection risk: string concatenation with variables in queries (contains SELECT/INSERT/UPDATE/DELETE with +)
    if (/\b(query|sql)\s*[:=]\s*`[^`]*\$\{[^}]+\}[^`]*`/.test(ln) || /\b(query|sql)\s*[:=]\s*['\"][^'\"]*\+\s*\w+/.test(ln)) {
      findings.push(report(fp, lineNum, ln.search(/\+/), '🟠', 'SQLInjectionRisk', 'Potential SQL injection via string concatenation', ln.trim()));
    }
    // Async issues: missing await on promise-returning calls (very heuristic: function call ending with .then without await)
    if (/\.then\(/.test(ln) && !/await\s+/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('.then'), '🟠', 'AsyncAwaitIssue', 'Promise .then used without await', ln.trim()));
    }
    // Error handling: catch blocks that are empty
    if (/catch\s*\(.*\)\s*\{\s*\}/.test(ln)) {
      findings.push(report(fp, lineNum, ln.indexOf('catch'), '🟠', 'EmptyCatch', 'Empty catch block may swallow errors', ln.trim()));
    }
  });
});

console.log(JSON.stringify(findings, null, 2));
