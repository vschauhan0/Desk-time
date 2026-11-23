// Connects to the NISS SignalR hub as an admin client and counts the live messages it receives.
// Usage: node signalr-probe.mjs <apiBaseUrl> <accessToken> <outputJsonPath> [seconds]
// Resolved from the dashboard's node_modules (run with that folder as the working directory).
import { createRequire } from 'node:module';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

// ES modules resolve packages relative to this file; use the current working directory (the dashboard folder) instead.
const require = createRequire(join(process.cwd(), 'noop.js'));
const { HubConnectionBuilder, LogLevel, HttpTransportType } = require('@microsoft/signalr');

const [baseUrl, token, out, secs] = process.argv.slice(2);
const seconds = Number(secs ?? 20);
const counts = { DeviceStateChanged: 0, EventIngested: 0, DeviceStatusChanged: 0, SecurityEventRaised: 0 };

const connection = new HubConnectionBuilder()
  .withUrl(`${baseUrl}/hubs/monitor`, { accessTokenFactory: () => token, transport: HttpTransportType.WebSockets, skipNegotiation: true })
  .configureLogging(LogLevel.Warning)
  .build();

for (const name of Object.keys(counts)) connection.on(name, () => { counts[name]++; });

try {
  await connection.start();
  await connection.invoke('SubscribeFeed');
  await new Promise((r) => setTimeout(r, seconds * 1000));
  await connection.stop();
  const received = Object.values(counts).reduce((a, b) => a + b, 0);
  writeFileSync(out, JSON.stringify({ ok: true, received, counts }));
} catch (e) {
  writeFileSync(out, JSON.stringify({ ok: false, received: 0, error: String(e) }));
  process.exitCode = 1;
}
