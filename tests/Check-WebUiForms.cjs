const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '../src/LeafHotKey/wwwroot');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');

assert.equal(html.includes('id="json"'), false, 'The WebUI must not expose a raw JSON editor.');
for (const id of ['ime-disable', 'send-delay', 'default-blind', 'profile-search', 'profile-dialog', 'rule-list']) {
  assert.ok(html.includes(`id="${id}"`), `Missing form control: ${id}`);
}
for (const handler of ['openProfileEditor', 'applyProfileEditor', 'syncSettingsFromForms', 'renderProfiles']) {
  assert.ok(app.includes(`function ${handler}`), `Missing form handler: ${handler}`);
}
console.log('PASS WebUI exposes form controls instead of a raw JSON editor');
