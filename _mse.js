const ws = new WebSocket('ws://127.0.0.1:8765');
ws.binaryType = 'arraybuffer';
let init = false, fmp4 = 0, h264 = 0, jpeg = 0, info = null;
ws.onopen = () => ws.send(JSON.stringify({ type: 'auth', password: '123456', codec: 'mse' }));
ws.onmessage = e => {
  if (typeof e.data === 'string') {
    const m = JSON.parse(e.data);
    if (m.type === 'video_info') info = m;
    if (m.mode) console.log('auth_ok mode=', m.mode);
    if (m.type === 'fmp4_init') init = true;
    return;
  }
  const t = new Uint8Array(e.data)[0];
  if (t === 3) fmp4++; else if (t === 0 || t === 1) h264++; else if (t === 2) jpeg++;
};
setTimeout(() => {
  console.log('9 秒统计: mse_init=' + init + ' fMP4分片=' + fmp4 + ' h264帧=' + h264 + ' jpeg帧=' + jpeg);
  console.log('video_info:', info ? (info.codec + ' ' + info.width + 'x' + info.height + ' desc=' + (info.description||'').length) : '无');
  ws.close(); process.exit(0);
}, 9000);
