import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/RtsSkillStudio.Api/wwwroot/app.js', import.meta.url), 'utf8');
function fixture() {
  const fields = new Map();
  const field = id => {
    if (!fields.has(id)) fields.set(id, {value:'', innerHTML:'', textContent:'', addEventListener(){}});
    return fields.get(id);
  };
  const state = {providers:[
    {name:'cloud', model:'cloud-model', requiresApiKey:true, apiKeyConfigured:false, reasoningEfforts:[]},
    {name:'local', model:'local-4b', requiresApiKey:false, reasoningEfforts:[]},
  ], selectedProvider:'cloud', selectedModel:'cloud-model'};
  const pending = [];
  const context = vm.createContext({state, document:{querySelector:field},
    elements:{reasoningSelect:field('#reasoningSelect')},
    window:{localStorage:{setItem(){}}},
    providerLabel:p=>p.name, setProviderStatus(){}, escapeHtml:s=>s, showToast(){},
    fetch:()=>new Promise(resolve=>pending.push(resolve)),
  });
  vm.runInContext(source.slice(source.indexOf('let modelListRequest'), source.indexOf('async function loadWorkspace()'))
    + source.slice(source.indexOf('function selectProvider('), source.indexOf('function showEmptyState('))
    + ';globalThis.api={selectProvider,loadModels,fillModelSettings};', context);
  return {state, pending, field, api:context.api};
}

test('switching provider changes model immediately even while model enumeration is pending', () => {
  const {state, api, pending, field} = fixture();
  api.selectProvider('local');
  assert.equal(pending.length, 1);
  assert.equal(state.selectedModel, 'local-4b');
  assert.equal(state.selectedProvider, 'local');
  assert.equal(field('#modelNameInput').value, 'local-4b');
});

test('late model responses do not overwrite another provider or a manually entered model', async () => {
  const {state, api, pending, field} = fixture();
  api.selectProvider('local');
  api.selectProvider('cloud');
  assert.equal(pending.length, 1); // Missing cloud key skips network enumeration.
  pending[0]({ok:true,json:async()=>[{name:'local-4b'}]});
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(state.selectedModel, 'cloud-model');
  assert.equal(field('#modelSuggestions').innerHTML, '');
  api.selectProvider('local');
  field('#modelNameInput').value = 'manual-model';
  pending[1]({ok:true,json:async()=>[{name:'local-4b'}]});
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(field('#modelNameInput').value, 'manual-model');
});


test('saved key displays matching stars without submitting the display mask as a credential', () => {
  const {state, api, field} = fixture();
  const provider = state.providers[0];
  provider.apiKeyConfigured = true;
  provider.apiKeyLength = 32;
  provider.apiKeySource = 'Studio';
  api.fillModelSettings(provider);
  assert.equal(field('#apiKeyInput').placeholder, '*'.repeat(32));
  assert.equal(field('#apiKeyInput').value, '');
  assert.match(field('#apiKeySource').textContent, /已保存/);
  api.selectProvider('local');
  assert.equal(field('#apiKeyInput').placeholder, '输入 API Key');
  assert.equal(field('#apiKeyInput').value, '');
});
