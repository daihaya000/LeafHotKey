const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '../src/LeafHotKey/wwwroot');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');

assert.equal(html.includes('id="json"'), false, 'The WebUI must not expose a raw JSON editor.');
for (const id of ['ime-disable', 'profile-search', 'add-profile', 'rule-list', 'rule-filter', 'process-add-name', 'process-list', 'profile-detail-title', 'profile-name', 'profile-processes']) {
  assert.ok(html.includes(`id="${id}"`), `Missing form control: ${id}`);
}
for (const handler of ['openProfileEditor', 'applyProfileEditor', 'addProfile', 'deleteProfile', 'syncSettingsFromForms', 'renderProfiles', 'renderProcessList', 'addProcess', 'removeProcess']) {
  assert.ok(app.includes(`function ${handler}`), `Missing form handler: ${handler}`);
}
assert.equal(html.includes('backend-mode'), false, 'The removed AHK backend must not come back into the UI.');
console.log('PASS WebUI exposes form controls instead of a raw JSON editor');
