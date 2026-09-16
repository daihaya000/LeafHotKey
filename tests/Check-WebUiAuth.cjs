// Run: node tests/Check-WebUiAuth.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/LeafHotKey/wwwroot/app.js'), 'utf8');
const reachedDom = new Error('DOM initialization');
const location = { href: 'http://127.0.0.1:12345/?token=test-only-token' };
// Execute the real authentication bootstrap, stopping before unrelated DOM rendering.
for (let load = 0; load < 2; load++) {
  const context = {
    URL,
    window: {
      location,
      history: { replaceState(_state, _title, url) { location.href = new URL(url, location.href).href; } },
    },
    document: { getElementById() { throw reachedDom; } },
  };
  assert.throws(() => vm.runInNewContext(source, context), (error) => error === reachedDom);
  assert.equal(new URL(location.href).searchParams.get('token'), 'test-only-token',
    'Reload must retain the token required by the authenticated HTML endpoint');
}
console.log('PASS WebUI initial load and reload retain authentication');
