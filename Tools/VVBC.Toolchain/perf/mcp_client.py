"""Minimal stdio MCP client for the Tsukimi MCP server shipped in the installed compiler package."""
import json
from pathlib import Path
import subprocess


class McpError(RuntimeError):
    pass


def server_dll(root):
    dll = Path(root) / 'Packages' / 'com.tsukimicode.udon-tools' / 'Server~' / 'TsukimiCode.Mcp.dll'
    if not dll.is_file():
        raise McpError('Tsukimi MCP server not found (install com.tsukimicode.udon-tools 0.10.1): ' + str(dll))
    return dll


class Client:
    def __init__(self, root, timeout=None):
        self.root = Path(root)
        self.proc = subprocess.Popen(
            ['dotnet', str(server_dll(self.root)), str(self.root)], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8')
        self._id = 0
        self.rpc('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                                'clientInfo': {'name': 'vvbc-perf', 'version': '1'}})
        self._send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})

    def _send(self, message):
        self.proc.stdin.write(json.dumps(message) + '\n')
        self.proc.stdin.flush()

    def rpc(self, method, params):
        self._id += 1
        self._send({'jsonrpc': '2.0', 'id': self._id, 'method': method, 'params': params})
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise McpError('Tsukimi MCP server closed the connection')
            message = json.loads(line)
            if message.get('id') == self._id:
                return message

    def call(self, tool, arguments):
        reply = self.rpc('tools/call', {'name': tool, 'arguments': arguments})
        if 'error' in reply:
            raise McpError(str(reply['error']))
        text = ''.join(part.get('text', '') for part in reply['result']['content'])
        try:
            return json.loads(text)
        except ValueError:
            raise McpError('Non-JSON reply from tool ' + tool + ': ' + text[:300])

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.terminate()
        except OSError:
            pass
