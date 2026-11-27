#!/usr/bin/env bash
set -x
WS=/var/tmp/sqzrlite-t3-fixture
HOME_PIN=/var/tmp/sqzrlite-t3-home
rm -rf $HOME_PIN && mkdir -p $HOME_PIN
export SQUEEZR_HOME=$HOME_PIN
export NO_COLOR=1
DLL=/mnt/Data/Git/Kit/Squeezr/.claude/worktrees/130-sqzrlite/src/Squeezr.Lite/bin/Debug/net10.0/sqzrlite.dll
SQZRL="dotnet $DLL"
cd $WS
$SQZRL init --json
$SQZRL add repo --name fixture
time $SQZRL index --json
echo "=== search (identifier) ==="
$SQZRL search "LedgerEntry" --top-k 3 --json
echo "=== search (regex, docs) ==="
$SQZRL search "Operation 1[0-9] is handled" --regex --kind docs --top-k 2 --json
echo "=== search (prefix scope) ==="
$SQZRL search "RateLimiter" --prefix docs/ --json | tail -1
echo "=== map ==="
$SQZRL map --repo fixture --depth 1 --json
echo "=== symbol ==="
$SQZRL symbol "OrderService42" --json
echo "=== refs (class with callsites) ==="
$SQZRL refs "OrderServiceHelper42" --json
echo "=== refs (class with none) ==="
$SQZRL refs "LedgerEntry2" --json
echo "=== span ==="
$SQZRL span "Gen066.cs" 6 10 --json
echo "=== pack ==="
$SQZRL pack "OrderServiceHelper42" --max-tokens 900 --json
echo "=== status ==="
$SQZRL status --json
echo "=== serve smoke (real stdio process) ==="
$SQZRL serve 2>serve-stderr.log <<'MCP' > serve-out.ndjson
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"0.0.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"code_search","arguments":{"query":"VaultClient","topK":2}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"index_status","arguments":{}}}
MCP
python3 - <<'SUMMARY'
import json
lines = [json.loads(l) for l in open("serve-out.ndjson") if l.strip()]
by_id = {m.get("id"): m for m in lines if "id" in m}
init = by_id[1]["result"]
print("serverInfo:", init["serverInfo"]["name"], init["serverInfo"]["version"])
tools = [t["name"] for t in by_id[2]["result"]["tools"]]
print("tools/list:", len(tools), sorted(tools))
search = by_id[3]["result"]
print("code_search isError:", search.get("isError", False))
envelope = json.loads(search["content"][0]["text"])
print("code_search hits:", len(envelope["data"]), "first:", envelope["data"][0]["file"], envelope["data"][0]["source"])
status = by_id[4]["result"]
status_envelope = json.loads(status["content"][0]["text"])
print("index_status semantic:", status_envelope["data"]["semantic"],
      "| has embeddings key:", "embeddings" in status_envelope["data"],
      "| has qdrant key:", "qdrant" in status_envelope["data"])
SUMMARY
echo "=== serve stderr ==="
cat serve-stderr.log
