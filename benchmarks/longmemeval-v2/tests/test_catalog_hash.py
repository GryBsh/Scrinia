from pathlib import Path
import sys,unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from batching import catalog_hash
class CatalogHashTests(unittest.TestCase):
    def test_dotnet_catalog_escape_vector(self):
        import hashlib
        expected=r'[{"description":"\u0027\u002B\u003C\u003E\u0026\u0060\u0022\u00E9","inputSchema":{"type":"object"},"name":"remember"}]'
        description="".join(chr(c) for c in [39,43,60,62,38,96,34,233])
        tools=[{"name":"remember","description":description,"inputSchema":{"type":"object"}}]
        self.assertEqual(catalog_hash(tools),hashlib.sha256(expected.encode()).hexdigest())
