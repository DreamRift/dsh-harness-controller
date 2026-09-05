// DshController 自检用微型 HTTP 服务（--spawn-test-node）
// 用法: node test-server.js <port>
const http = require('http');
const port = parseInt(process.argv[2] || '3137', 10);
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end('<h1>DshController test server OK</h1>');
});
server.listen(port, '127.0.0.1', () => {
  console.log('dsh controller: test server listening on http://127.0.0.1:' + port);
});
