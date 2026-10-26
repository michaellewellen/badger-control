const sliders = document.querySelectorAll('input[type=range]');

function showValue(el) {
    document.getElementById(el.id + 'Val').textContent = el.value;
}

async function load() {
    const settings = await (await fetch('/api/settings')).json();
    for (const el of sliders) {
        el.value = settings[el.CDATA_SECTION_NODE.key];
        showValue(el);
    }
}

async function save() {
    const bondy = {};
    for (const el of sliders) {
        body[el.CDATA_SECTION_NODE.key] = Number(el.value);
    }
    await fetch('/api/settings',{
        method: 'POST',
        headers: { 'Content-Ty;e' : 'application/json' },
        body: JSON.stringify(body)
    });
}
const SLIDERS = [
  { section: 'Economy' },
  { key: 'targetWorkers',          label: 'Target workers',                        min: 5,   max: 20 },
  { key: 'depotAtWorkers',         label: 'Home depot at N workers (0 = never)',   min: 0,   max: 18 },
  { key: 'scoutOreAtTick',         label: 'Scout for ore at tick (0 = never)',     min: 0,   max: 400 },
  { key: 'expandDepot',            label: 'Far depot (0 = off, 1 = on)',           min: 0,   max: 1 },
  { key: 'expandWhenHomeOreBelow', label: 'Far depot when nearby ore left <',      min: 0,   max: 3000 },
  { key: 'lateWorkers',            label: 'Extra workers later (+N)',              min: 0,   max: 10 },
  { key: 'lateWorkersAtTick',      label: '...starting at tick',                   min: 100, max: 900 },
  { section: 'Production' },
  { key: 'barracksCount',          label: 'Planned Barracks (by worker count)',    min: 1,   max: 4 },
  { key: 'firstBarracksAt',        label: '1st Barracks at N workers',             min: 4,   max: 12 },
  { key: 'extraBarracksAt',        label: 'Other planned Barracks at N workers',   min: 6,   max: 18 },
  { key: 'archerPercent',          label: 'Archers (% of army)',                   min: 0,   max: 100 },
  { key: 'bankBarracksAt',         label: 'Bonus Barracks when ore ≥ (0 = off)',   min: 0,   max: 1000 },
  { key: 'maxBarracks',            label: 'Max Barracks, planned + bonus',         min: 1,   max: 6 },
  { section: 'Tech' },
  { key: 'upgradeCount',           label: 'Upgrades (H1, W1, A1, W2, A2)',         min: 0,   max: 5 },
  { key: 'techLabAt',              label: 'Tech lab at N workers',                 min: 6,   max: 16 },
  { section: 'Defense' },
  { key: 'towerCount',             label: 'Home guard towers',                     min: 0,   max: 6 },
  { key: 'towerAt',                label: 'Towers at N workers',                   min: 8,   max: 18 },
  { key: 'farTower',               label: 'Tower at far depot (0/1)',              min: 0,   max: 1 },
  { key: 'defendPercent',          label: 'Garrison kept home (% of army)',        min: 0,   max: 100 },
  { section: 'Offense' },
  { key: 'attackAtTick',           label: 'Launch at tick (0 = by size only)',     min: 0,   max: 1800 },
  { key: 'attackThreshold',        label: 'Minimum troops to launch',              min: 1,   max: 100 },
  { key: 'raiderScout',            label: 'Rogue scout (0/1)',                     min: 0,   max: 1 },
];

const panel = document.getElementById('sliders');
let settings = {};

function show(key) {
  document.getElementById(key + 'Val').textContent = settings[key];
}

async function save() {
  await fetch('/api/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings),
  });
}

function build() {
  for (const def of SLIDERS) {
    if (def.section) {
      const h = document.createElement('h3');
      h.textContent = def.section;
      panel.appendChild(h);
      continue;
    }

    const row = document.createElement('div');
    row.className = 'row';
    row.innerHTML =
      `<label for="${def.key}">${def.label}: <b id="${def.key}Val"></b></label>
       <input type="range" id="${def.key}" min="${def.min}" max="${def.max}" value="${settings[def.key]}">`;
    panel.appendChild(row);
    show(def.key);

    row.querySelector('input').addEventListener('input', e => {
      settings[def.key] = Number(e.target.value);
      show(def.key);
      save();
    });
  }
}

async function load() {
  const response = await fetch('/api/settings');
  settings = await response.json();   // the server's real values, so sliders can't lie
  build();
}

document.getElementById('start').addEventListener('click', async () => {
  const response = await fetch('/api/start', { 
    method: 'POST',
    headers: { 'Content-Type': 'application/json'}  ,
    body: JSON.stringify({ opponent: document.getElementById('opponent').value}),
  });
  if (!response.ok) { alert('Could not start a match.'); return; }
  const { matchId } = await response.json();
  document.getElementById('game').src = `https://NetRTS.snowse.io/?t=${Date.now()}#/match/${matchId}`;
});

document.getElementById('join').addEventListener('click', async () => {
  const name = document.getElementById('matchName').value.trim();
  if (!name) { return; }
  const response = await fetch('/api/join', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ matchName: name }),
  });
  const data = await response.json();
  if (!response.ok) { alert(data.error ?? 'Could not join.'); return; }
  document.getElementById('game').src = `https://NetRTS.snowse.io/?t=${Date.now()}#/match/${name}`;
});

load();