// A TCP proxy that behaves like an internet link: adds one-way delay and caps bandwidth in each
// direction. Used by stream.mjs to see how the stream copes with real-world connections.
//   node netsim.mjs <listenPort> <targetPort> <rttMs> <downMbit> [upMbit]
import net from 'node:net';

export function startLink({ listenPort, targetPort, rttMs, downMbit, upMbit = 20 }) {
  const oneWay = rttMs / 2;
  const server = net.createServer((client) => {
    const upstream = net.connect(targetPort, '127.0.0.1');
    // Real browsers and the mod both disable Nagle; the simulated link must not add its own batching.
    client.setNoDelay(true);
    upstream.setNoDelay(true);
    pipe(client, upstream, upMbit);   // guest -> host
    pipe(upstream, client, downMbit); // host -> guest
    const kill = () => { client.destroy(); upstream.destroy(); };
    client.on('error', kill);
    upstream.on('error', kill);
  });
  // Each chunk leaves when the link has finished sending the previous ones (bandwidth) and then
  // arrives oneWay ms later (latency). One queue per direction keeps the bytes in order.
  function pipe(from, to, mbit) {
    const bytesPerMs = (mbit * 1e6) / 8 / 1000;
    let linkFreeAt = 0;
    const queue = [];
    let timer = null;
    let ended = false;
    const pump = () => {
      timer = null;
      const now = performance.now();
      while (queue.length && queue[0].due <= now) {
        const { chunk } = queue.shift();
        if (!to.destroyed) to.write(chunk);
      }
      if (queue.length) timer = setTimeout(pump, Math.max(0, queue[0].due - now));
      else if (ended && !to.destroyed) to.end();
    };
    from.on('data', (chunk) => {
      const now = performance.now();
      const start = Math.max(now, linkFreeAt);
      linkFreeAt = start + chunk.length / bytesPerMs;
      queue.push({ due: linkFreeAt + oneWay, chunk });
      if (!timer) timer = setTimeout(pump, Math.max(0, queue[0].due - now));
    });
    // Pass the close on only after the data still "on the wire" has arrived.
    from.on('end', () => {
      ended = true;
      if (!timer) pump();
    });
  }
  server.listen(listenPort, '127.0.0.1');
  return server;
}

if (process.argv[1] && process.argv[1].endsWith('netsim.mjs')) {
  const [listenPort, targetPort, rttMs, downMbit, upMbit] = process.argv.slice(2).map(Number);
  startLink({ listenPort, targetPort, rttMs, downMbit, upMbit });
  console.log(`link on ${listenPort} -> ${targetPort}: ${rttMs} ms RTT, ${downMbit} Mbit/s down`);
}
