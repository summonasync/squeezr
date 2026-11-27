#!/usr/bin/env bash
# T5 verification: the PUBLISHED sqzrlite single-file binary (trim-less
# self-contained + ReadyToRun) runs the full end-to-end on this box.
# Same fixture as T3 (/var/tmp/sqzrlite-t3-fixture/repo, 150 files) so numbers compare.
set -x
WS=/var/tmp/sqzrlite-t3-fixture
HOME_PIN=/var/tmp/sqzrlite-t5-home
rm -rf $HOME_PIN && mkdir -p $HOME_PIN
export SQUEEZR_HOME=$HOME_PIN
export NO_COLOR=1
BIN=/mnt/Data/Git/Kit/Squeezr/.claude/worktrees/130-sqzrlite/artifacts/publish-sqzrlite-linux-x64/sqzrlite
SQZRL=$BIN

echo "=== box load ==="
uptime
nproc
free -m | head -2

echo "=== version stamp ==="
$SQZRL --version

cd $WS
echo "=== init ==="
$SQZRL init --json
echo "=== add ==="
$SQZRL add repo --name fixture
echo "=== index ==="
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
echo "=== refs ==="
$SQZRL refs "OrderServiceHelper42" --json
echo "=== span ==="
$SQZRL span "Gen066.cs" 6 10 --json
echo "=== pack ==="
$SQZRL pack "OrderServiceHelper42" --max-tokens 900 --json
echo "=== status ==="
$SQZRL status --json

echo "=== startup: one-shot map, cold, small fixture, 5 runs ==="
SMALL=/var/tmp/sqzrlite-t5-small
rm -rf $SMALL && mkdir -p $SMALL/repo/src
for i in 1 2 3 4 5 6 7 8; do
  cat > $SMALL/repo/src/Type$i.cs <<EOF
public class Type$i
{
    public int Value => $i;
    public string Name() => "Type$i";
}
EOF
done
cd $SMALL
$SQZRL init --json >/dev/null
$SQZRL add repo --name repo >/dev/null
$SQZRL index --json >/dev/null
for run in 1 2 3 4 5; do
  /usr/bin/time -f "map run $run: %es wall, %MKB peak" $SQZRL map --repo repo --json >/dev/null
done

echo "=== serve smoke (real stdio process) + time-to-first-answer ==="
cd $WS
$SQZRL serve 2>serve-stderr.log <<'MCP' > serve-out.ndjson
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"0.0.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"code_search","arguments":{"query":"VaultClient","topK":2}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"index_status","arguments":{}}}
MCP
python3 - <<'SUMMARY'
import json, subprocess, time, os
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

bin = "/mnt/Data/Git/Kit/Squeezr/.claude/worktrees/130-sqzrlite/artifacts/publish-sqzrlite-linux-x64/sqzrlite"
env = dict(os.environ, SQUEEZR_HOME="/var/tmp/sqzrlite-t5-home")
loads = []
for run in range(3):
    t0 = time.perf_counter()
    p = subprocess.Popen([bin, "serve"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, env=env)
    p.stdin.write(b'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"0.0.0"}}}\n')
    p.stdin.flush()
    p.stdout.readline()
    t1 = time.perf_counter()
    p.stdin.write(b'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"code_search","arguments":{"query":"VaultClient","topK":2}}}\n')
    p.stdin.flush()
    while True:
        line = p.stdout.readline()
        if not line:
            break
        msg = json.loads(line)
        if msg.get("id") == 2:
            break
    t2 = time.perf_counter()
    p.stdin.close(); p.terminate(); p.wait()
    loads.append((t1 - t0, t2 - t0))
for i, (hand, first) in enumerate(loads, 1):
    print(f"serve run {i}: handshake {hand:.3f}s, serve-to-first-answer {first:.3f}s")
SUMMARY

echo "=== serve stderr ==="
cat serve-stderr.log
