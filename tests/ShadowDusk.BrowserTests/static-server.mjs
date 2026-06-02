// Minimal static file server for the published Blazor wwwroot.
// No dependencies; serves with the few MIME types the WASM app needs and the
// COOP/COEP-free defaults KNI uses. Returns { url, close }.
import http from 'node:http';
import { promises as fs } from 'node:fs';
import path from 'node:path';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.wasm': 'application/wasm',
  '.css': 'text/css; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.dat': 'application/octet-stream',
  '.blat': 'application/octet-stream',
  '.mgfx': 'application/octet-stream',
  '.fx': 'text/plain; charset=utf-8',
  '.dll': 'application/octet-stream',
  '.pdb': 'application/octet-stream',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.ico': 'image/x-icon',
  '.map': 'application/json',
};

export async function startServer(rootDir) {
  const root = path.resolve(rootDir);
  const server = http.createServer(async (req, res) => {
    try {
      let urlPath = decodeURIComponent((req.url || '/').split('?')[0]);
      if (urlPath === '/' || urlPath === '') urlPath = '/index.html';
      let filePath = path.join(root, urlPath);
      // Prevent path traversal.
      if (!filePath.startsWith(root)) {
        res.writeHead(403).end('forbidden');
        return;
      }
      let data;
      try {
        data = await fs.readFile(filePath);
      } catch {
        // SPA fallback to index.html for unknown routes.
        filePath = path.join(root, 'index.html');
        data = await fs.readFile(filePath);
      }
      const ext = path.extname(filePath).toLowerCase();
      res.writeHead(200, { 'Content-Type': MIME[ext] || 'application/octet-stream' });
      res.end(data);
    } catch (e) {
      res.writeHead(500).end(String(e));
    }
  });

  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  return {
    url: `http://127.0.0.1:${port}`,
    close: () => new Promise((r) => server.close(r)),
  };
}
