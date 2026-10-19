import { createServer } from 'node:net';

/**
 * Proof that no test reaches the real API: listen on its port (5291, IPv4 and IPv6) for the whole run and
 * fail the run if any connection arrives. If the port is taken (your API is running), the tests still run,
 * because every request is answered in the browser, but this proof is skipped and a warning is printed.
 */
export default async function tripwire(): Promise<(() => Promise<void>) | void> {
  let hits = 0;
  const server = createServer((socket) => { hits++; socket.destroy(); });
  const bound = await new Promise<boolean>((resolve) => {
    server.once('error', () => resolve(false));
    server.listen({ port: 5291, host: '::', ipv6Only: false }, () => resolve(true));
  });
  if (!bound) {
    console.warn('[tripwire] port 5291 is in use (your API?): requests are still mocked, but the no-traffic proof is skipped.');
    return;
  }
  console.log('[tripwire] listening on :5291; any connection fails the run.');
  return async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()));
    console.log(`[tripwire] connections to :5291 during the run: ${hits}`);
    if (hits > 0) throw new Error(`${hits} connection(s) reached port 5291: a request escaped the mocks.`);
  };
}
