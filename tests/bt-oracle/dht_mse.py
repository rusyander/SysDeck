# Windows Process Cleaner - torrent lane C oracle: libtorrent 2.x as an independent MSE/PE and DHT implementation.
# Started by tests/Tests.TorrentDht.cs with WPC_LT_PYTHON. Binds 127.0.0.1 only; DHT bootstrap, LSD, UPnP, NAT-PMP off.
#   python dht_mse.py mse <workdir>   seeds a v1 torrent with encryption forced (RC4 only, both directions)
#   python dht_mse.py dht <workdir>   a lone DHT node, routing/search IP restrictions off
# stdout lines: "PORT n", "HASH hex" (mse), "READY"; replies to commands: "OK ...", "PEERS a:p,b:p", "ERR ...".
# stdin commands: "connect <port>" (mse: lt dials us), "addnode <port>", "announce <hex> <port>", "getpeers <hex>",
# "quit". The process exits on stdin EOF or after 90 s, whichever comes first.
import os
import random
import sys
import threading
import time

import libtorrent as lt


def out(line):
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def base_settings(dht):
    return {
        'listen_interfaces': '127.0.0.1:0',
        'outgoing_interfaces': '127.0.0.1',
        'enable_lsd': False,
        'enable_upnp': False,
        'enable_natpmp': False,
        'enable_dht': dht,
        'dht_bootstrap_nodes': '',
        'alert_mask': lt.alert.category_t.all_categories,
    }


def wait_port(ses):
    # Windows hands out port 0 sequentially; inside a run of numbers excluded for one protocol (Hyper-V) every retry
    # fails, so after 2 s a random number from the dynamic range is tried.
    for _ in range(10):
        for _ in range(40):
            p = ses.listen_port()
            if p:
                return p
            time.sleep(0.05)
        ses.apply_settings({'listen_interfaces': '127.0.0.1:%d' % random.randint(49152, 65535)})
    return 0


def make_seed(ses, work):
    data_dir = os.path.join(work, 'lt-seed')
    os.makedirs(data_dir, exist_ok=True)
    path = os.path.join(data_dir, 'payload.bin')
    with open(path, 'wb') as f:
        f.write(bytes((i * 31 + 7) & 0xFF for i in range(200000)))
    fs = lt.file_storage()
    lt.add_files(fs, path)
    ct = lt.create_torrent(fs, 32768, flags=lt.create_torrent.v1_only)
    lt.set_piece_hashes(ct, data_dir)
    ti = lt.torrent_info(ct.generate())
    h = ses.add_torrent({'ti': ti, 'save_path': data_dir})
    for _ in range(200):
        if h.status().state == lt.torrent_status.seeding:
            break
        time.sleep(0.05)
    return h, str(ti.info_hashes().v1)


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ('mse', 'dht'):
        out("ERR usage")
        return 2
    mode, work = sys.argv[1], sys.argv[2]
    threading.Timer(90, lambda: os._exit(3)).start()
    s = base_settings(mode == 'dht')
    if mode == 'mse':
        s.update({'out_enc_policy': int(lt.enc_policy.forced), 'in_enc_policy': int(lt.enc_policy.forced),
                  'allowed_enc_level': int(lt.enc_level.rc4), 'prefer_rc4': True,
                  'enable_outgoing_utp': False, 'enable_incoming_utp': False, 'allow_multiple_connections_per_ip': True})
    else:
        s.update({'dht_restrict_routing_ips': False, 'dht_restrict_search_ips': False, 'dht_ignore_dark_internet': False,
                  'dht_block_ratelimit': 1000, 'dht_prefer_verified_node_ids': False, 'dht_enforce_node_id': False})
    ses = lt.session(s)
    port = wait_port(ses)
    if not port:
        out("ERR no listen port")
        return 1
    handle = None
    if mode == 'mse':
        handle, ih = make_seed(ses, work)
        out("HASH " + ih)
    out("PORT %d" % port)
    out("READY")
    for raw in sys.stdin:
        parts = raw.strip().split()
        if not parts:
            continue
        cmd = parts[0]
        try:
            if cmd == 'quit':
                break
            elif cmd == 'connect' and handle is not None:
                handle.connect_peer(('127.0.0.1', int(parts[1])))
                out("OK connect")
            elif cmd == 'addnode':
                ses.add_dht_node(('127.0.0.1', int(parts[1])))
                out("OK addnode")
            elif cmd == 'announce':
                ses.dht_announce(lt.sha1_hash(bytes.fromhex(parts[1])), int(parts[2]), 0)
                out("OK announce")
            elif cmd == 'getpeers':
                target = parts[1].lower()
                ses.dht_get_peers(lt.sha1_hash(bytes.fromhex(target)))
                found = set()
                deadline = time.time() + 5
                while not found and time.time() < deadline:
                    ses.wait_for_alert(200)
                    for a in ses.pop_alerts():
                        if isinstance(a, lt.dht_get_peers_reply_alert) and str(a.info_hash) == target:
                            found.update("%s:%d" % (ep[0], ep[1]) for ep in a.peers())
                out("PEERS " + ",".join(sorted(found)))
            else:
                out("ERR unknown " + cmd)
        except Exception as e:  # the C# side reads a reply line for every command
            out("ERR " + type(e).__name__ + " " + str(e))
    return 0


if __name__ == '__main__':
    code = main()
    sys.stdout.flush()
    os._exit(code)
