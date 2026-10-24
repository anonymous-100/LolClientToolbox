#!/usr/bin/env python3
"""重新生成探针夹具 allgamedata_sample.json。

要求：正有一局对局在进行（Live Client Data API 只在对局中存在）。
人名会一致地替换成「玩家N」，避免把真实玩家 ID 提交进仓库。

用法：
  python tools/probe/make_fixture.py
"""
import json
import socket
import ssl
import sys

OUT = __file__.rsplit("\\", 1)[0].replace("/", "\\") + "\\testdata\\allgamedata_sample.json"
# 兼容两种分隔符
import os
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "testdata", "allgamedata_sample.json")


def get(path: str) -> str:
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    s = ctx.wrap_socket(socket.create_connection(("127.0.0.1", 2999), timeout=3),
                        server_hostname="127.0.0.1")
    s.sendall(f"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:2999\r\nConnection: close\r\n\r\n".encode())
    data = b""
    while True:
        b = s.recv(65536)
        if not b:
            break
        data += b
    s.close()
    return data.decode("utf-8", "replace").split("\r\n\r\n", 1)[1]


def main() -> int:
    try:
        j = json.loads(get("/liveclientdata/allgamedata"))
    except Exception as e:
        print(f"✕ 取不到 allgamedata（没在对局中？）：{e}")
        return 1

    # 一致地替换人名 → 玩家N；activePlayer 就是 玩家1
    names: dict[str, str] = {}

    def nm(n: str) -> str:
        if n not in names:
            names[n] = f"玩家{len(names) + 1}"
        return names[n]

    me = j["activePlayer"]
    me["riotIdGameName"] = nm(me.get("riotIdGameName", ""))
    me["riotIdTagLine"] = "0001"
    me["summonerName"] = me["riotIdGameName"]
    if "riotId" in me:
        me["riotId"] = me["riotIdGameName"] + "#0001"
    for i, p in enumerate(j["allPlayers"]):
        for k in ("riotIdGameName", "summonerName"):
            if k in p:
                p[k] = nm(p[k])
        p["riotIdTagLine"] = "%04d" % (i + 2)
        if "riotId" in p and "#" in str(p["riotId"]):
            g, _, _ = str(p["riotId"]).partition("#")
            p["riotId"] = names.get(g, g) + "#%04d" % (i + 2)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(j, f, ensure_ascii=False, indent=1)

    mine = [p for p in j["allPlayers"] if p["riotIdGameName"] == me["riotIdGameName"]]
    print(f"✓ 夹具已写：{OUT}")
    print(f"  自己 = {me['riotIdGameName']}，respawnTimer = {mine[0]['respawnTimer'] if mine else '??'}")
    print(f"  全表 = {[round(p['respawnTimer'], 1) for p in j['allPlayers']]}")
    print("  注意：探针断言「自己解析出 0.0」—— 如果你抓样本时自己正好死了，")
    print("  请把 Checks.LiveParse 里的期望值同步改掉，或者等活着的时候再抓一次。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
