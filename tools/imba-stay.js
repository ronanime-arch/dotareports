// Не даёт карте IMBA AWAKEN (воркшоп 3320645051) выкидывать из матча в главное меню
// сразу после конца игры, чтобы можно было посмотреть итоги на экране самой карты.
//
// По сигналу сервера GameEndDisconnect клиентский скрипт карты
// scripts/vscripts/addon_game_mode_client.lua выполняет SendToConsole("disconnect").
// Скрипт меняет в VPK команду "disconnect" на безвредную "echo stay." той же длины,
// поэтому смещения в файле не сдвигаются, затем пересчитывает CRC файла,
// MD5 каталога и MD5 всего VPK и возвращает файлу прежнюю дату изменения
// (NEC кэширует карту по размеру и дате).
//
//   node imba-stay.js status | on | off   [--vpk <путь>] [--skip-dota-check]

'use strict';
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');

const DEFAULT_VPK = 'C:\\Program Files (x86)\\Steam\\steamapps\\workshop\\content\\570\\3320645051\\3320645051.vpk';
const ENTRY = 'scripts/vscripts/addon_game_mode_client.lua';
const PREFIX = 'GameEvent("awaken_game_end_disconnect",function()SendToConsole("';
const ORIGINAL = Buffer.from(PREFIX + 'disconnect")end', 'latin1');
const PATCHED = Buffer.from(PREFIX + 'echo stay.")end', 'latin1');
const BACKUP = path.join(__dirname, 'imba-stay-backup.json');

function fail(message) { console.error('Ошибка: ' + message); process.exit(1); }

function args() {
  const list = process.argv.slice(2);
  const opts = { command: 'status', vpk: DEFAULT_VPK, skipDotaCheck: false };
  for (let i = 0; i < list.length; i++) {
    if (list[i] === '--vpk') opts.vpk = list[++i];
    else if (list[i] === '--skip-dota-check') opts.skipDotaCheck = true;
    else opts.command = list[i];
  }
  if (!['status', 'on', 'off'].includes(opts.command)) fail('команда: status, on или off');
  return opts;
}

function dotaRunning() {
  try {
    return /dota2\.exe/i.test(execFileSync('tasklist.exe', ['/FI', 'IMAGENAME eq dota2.exe', '/NH'], { encoding: 'latin1' }));
  } catch { return false; }
}

function read(fd, pos, len) {
  const b = Buffer.alloc(len);
  if (fs.readSync(fd, b, 0, len, pos) !== len) fail('файл карты обрезан');
  return b;
}

function md5Range(fd, end) {
  const hash = crypto.createHash('md5'), chunk = 64 << 20;
  for (let pos = 0; pos < end; pos += chunk) hash.update(read(fd, pos, Math.min(chunk, end - pos)));
  return hash.digest();
}

// Формат VPK v2: заголовок 28 байт, каталог, данные, таблица MD5 кусков,
// блок из трёх MD5 (каталог, таблица кусков, весь файл до этого места), подпись.
function inspect(fd) {
  const h = read(fd, 0, 28);
  if (h.readUInt32LE(0) !== 0x55aa1234 || h.readUInt32LE(4) !== 2) fail('это не VPK версии 2');
  const treeSize = h.readUInt32LE(8), dataSize = h.readUInt32LE(12), chunkMd5Size = h.readUInt32LE(16);
  if (h.readUInt32LE(20) !== 48) fail('неизвестный формат блока MD5');
  const tree = read(fd, 28, treeSize);
  let p = 0;
  const str = () => { const e = tree.indexOf(0, p); if (e < 0) fail('битый каталог VPK'); const s = tree.toString('latin1', p, e); p = e + 1; return s; };
  let entry = null;
  for (;;) {
    const ext = str(); if (!ext) break;
    for (;;) {
      const dir = str(); if (!dir) break;
      for (;;) {
        const name = str(); if (!name) break;
        const rec = { treePos: p, crc: tree.readUInt32LE(p), preload: tree.readUInt16LE(p + 4), archive: tree.readUInt16LE(p + 6),
          offset: tree.readUInt32LE(p + 8), length: tree.readUInt32LE(p + 12) };
        if (`${dir}/${name}.${ext}` === ENTRY) entry = rec;
        p += 18 + rec.preload;
      }
    }
  }
  if (!entry) fail(`в карте нет ${ENTRY} — неизвестная версия карты`);
  if (entry.archive !== 0x7fff || entry.preload !== 0) fail('скрипт карты лежит не внутри VPK — неизвестная версия карты');
  const dataStart = 28 + treeSize;
  const md5Pos = dataStart + dataSize + chunkMd5Size;
  const content = read(fd, dataStart + entry.offset, entry.length);
  const find = needle => { const i = content.indexOf(needle); return i >= 0 && content.indexOf(needle, i + 1) < 0 ? i : (i >= 0 ? -2 : -1); };
  const iOrig = find(ORIGINAL), iPatched = find(PATCHED);
  if (iOrig === -2 || iPatched === -2) fail('команда выхода встречается в скрипте несколько раз — неизвестная версия карты');
  const state = iOrig >= 0 && iPatched < 0 ? 'off' : iPatched >= 0 && iOrig < 0 ? 'on' : 'unknown';
  return { tree, treeSize, entry, dataStart, md5Pos, content, state, at: iOrig >= 0 ? iOrig : iPatched,
    md5Block: read(fd, md5Pos, 48) };
}

function checkSums(fd, info) {
  const crcOk = zlib.crc32(info.content) === info.entry.crc;
  const treeOk = crypto.createHash('md5').update(info.tree).digest().equals(info.md5Block.subarray(0, 16));
  const wholeOk = md5Range(fd, info.md5Pos + 32).equals(info.md5Block.subarray(32, 48));
  return { crcOk, treeOk, wholeOk, ok: crcOk && treeOk && wholeOk };
}

function setState(fd, info, target) {
  const replacement = target === 'on' ? PATCHED : ORIGINAL;
  fs.writeSync(fd, replacement, 0, replacement.length, info.dataStart + info.entry.offset + info.at);
  replacement.copy(info.content, info.at);
  const crc = Buffer.alloc(4); crc.writeUInt32LE(zlib.crc32(info.content));
  fs.writeSync(fd, crc, 0, 4, 28 + info.entry.treePos);
  crc.copy(info.tree, info.entry.treePos);
  fs.writeSync(fd, crypto.createHash('md5').update(info.tree).digest(), 0, 16, info.md5Pos);
  fs.writeSync(fd, md5Range(fd, info.md5Pos + 32), 0, 16, info.md5Pos + 32);
  fs.fsyncSync(fd);
}

function main() {
  const opts = args();
  if (!fs.existsSync(opts.vpk)) fail(`нет файла карты ${opts.vpk}`);
  const names = { on: 'ВКЛЮЧЕНО — карта не выкидывает после матча', off: 'выключено — карта как в оригинале',
    unknown: 'неизвестно — карта обновилась или изменена, правку не трогаю' };

  if (opts.command === 'status') {
    const fd = fs.openSync(opts.vpk, 'r');
    try {
      const info = inspect(fd);
      console.log(`Остаться на экране итогов IMBA: ${names[info.state]}`);
      const sums = checkSums(fd, info);
      if (!sums.ok) console.log(`Внимание: контрольные суммы не сходятся (CRC ${sums.crcOk}, каталог ${sums.treeOk}, файл ${sums.wholeOk}).`);
    } finally { fs.closeSync(fd); }
    return;
  }

  if (!opts.skipDotaCheck && dotaRunning()) fail('сначала закройте Dota 2 — пока она запущена, файл карты менять нельзя');
  const before = fs.statSync(opts.vpk);
  const fd = fs.openSync(opts.vpk, 'r+');
  try {
    const info = inspect(fd);
    if (info.state === 'unknown') fail(names.unknown);
    if (info.state === opts.command) { console.log(`Уже ${names[info.state]}.`); return; }
    const sums = checkSums(fd, info);
    if (!sums.ok) fail(`контрольные суммы карты не сходятся ещё до правки (CRC ${sums.crcOk}, каталог ${sums.treeOk}, файл ${sums.wholeOk}) — не трогаю`);

    if (opts.command === 'on') {
      // Оригинальные байты сохраняются до первой записи; off их не требует, но с ними
      // можно вручную вернуть карту и сверить результат.
      fs.writeFileSync(BACKUP, JSON.stringify({
        vpk: opts.vpk, size: before.size, mtimeMs: before.mtimeMs, savedAt: new Date().toISOString(),
        wholeMd5: info.md5Block.subarray(32, 48).toString('hex'),
        entryPos: info.dataStart + info.entry.offset, entry: info.content.toString('base64'),
        treeRecordPos: 28 + info.entry.treePos, treeRecord: info.tree.subarray(info.entry.treePos, info.entry.treePos + 18).toString('hex'),
        md5BlockPos: info.md5Pos, md5Block: info.md5Block.toString('hex')
      }, null, 1));
    }
    setState(fd, info, opts.command);
    const after = inspect(fd);
    if (after.state !== opts.command || !checkSums(fd, after).ok)
      fail('после записи проверка не прошла — верните карту: отпишитесь и снова подпишитесь на неё в мастерской Steam');
    if (opts.command === 'off' && fs.existsSync(BACKUP)) {
      const backup = JSON.parse(fs.readFileSync(BACKUP, 'utf8'));
      if (backup.size === before.size && backup.wholeMd5 !== after.md5Block.subarray(32, 48).toString('hex'))
        console.log('Внимание: карта выключена, но MD5 не совпал с сохранённым оригиналом — возможно, карта обновлялась.');
    }
  } finally { fs.closeSync(fd); }
  fs.utimesSync(opts.vpk, before.atime, before.mtime);
  console.log(`Готово: ${names[opts.command]}.`);
}

main();
