# Oracle for the lane B wire tests (Tests.TorrentWire.cs): libtorrent 2.1.1 as an independent implementation.
# Loopback only: listen 127.0.0.1, DHT/LSD/UPnP/NAT-PMP/uTP off, plaintext unless 'seedenc'.
#   wire.py seed  <torrent> <save_dir>          -> "PORT n", "SEEDING"; runs until stdin closes (cap 60 s), then "UPLOADED n"
#   wire.py seedenc <torrent> <save_dir>        -> as seed, RC4 forced both ways, plus "MAGNET <uri>" (Tests.TorrentSession.cs)
#   wire.py leech <torrent> <save_dir> <port>   -> dials 127.0.0.1:port, "DONE <downloaded bytes>" when finished, "FAIL why"
import random
import socket
import sys
import threading
import time

import libtorrent as lt


def free_port():
    # A port free for both UDP and TCP: Windows excludes port ranges per protocol (Hyper-V), and port 0 is handed out
    # sequentially, so a run of excluded numbers would swallow every retry - random numbers instead.
    for _ in range(50):
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        t = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            port = random.randint(49152, 65535)
            u.bind(('127.0.0.1', port))
            t.bind(('127.0.0.1', port))
            return port
        except OSError:
            pass
        finally:
            u.close()
            t.close()
    return 0


def make_session(forced=False):
    settings = {
        'listen_interfaces': '127.0.0.1:%d' % free_port(),
        'enable_dht': False,
        'enable_lsd': False,
        'enable_upnp': False,
        'enable_natpmp': False,
        'enable_incoming_utp': False,
        'enable_outgoing_utp': False,
        'out_enc_policy': 2,    # pe_disabled
        'in_enc_policy': 1,     # pe_enabled: plaintext accepted
        'allow_multiple_connections_per_ip': True,
        'alert_mask': 0,
    }
    if forced:
        settings.update({'out_enc_policy': 0, 'in_enc_policy': 0, 'allowed_enc_level': 2, 'prefer_rc4': True})   # pe_forced, pe_rc4
    ses = lt.session(settings)
    # The port may be taken between the probe and lt's bind: pick another one.
    for _ in range(10):
        deadline = time.time() + 2
        while ses.listen_port() == 0 and time.time() < deadline:
            time.sleep(0.05)
        if ses.listen_port() != 0:
            break
        ses.apply_settings({'listen_interfaces': '127.0.0.1:%d' % free_port()})
    return ses


def say(text):
    sys.stdout.write(text + '\n')
    sys.stdout.flush()


def add(ses, torrent, save_dir):
    atp = lt.add_torrent_params()
    atp.ti = lt.torrent_info(torrent)
    atp.save_path = save_dir
    return ses.add_torrent(atp)


def seed(torrent, save_dir, forced=False):
    ses = make_session(forced)
    h = add(ses, torrent, save_dir)
    deadline = time.time() + 20
    while not h.status().is_seeding:
        if time.time() > deadline:
            say('FAIL lt did not reach seeding: state %s progress %.3f' % (h.status().state, h.status().progress))
            return 1
        time.sleep(0.05)
    if ses.listen_port() == 0:
        say('FAIL lt has no listen port')
        return 1
    say('PORT %d' % ses.listen_port())
    if forced:
        say('MAGNET %s' % lt.make_magnet_uri(h))
    say('SEEDING')
    closed = threading.Event()

    def wait_stdin():
        try:
            sys.stdin.read()
        finally:
            closed.set()

    threading.Thread(target=wait_stdin, daemon=True).start()
    closed.wait(60)
    say('UPLOADED %d' % h.status().total_payload_upload)
    return 0


def leech(torrent, save_dir, port):
    ses = make_session()
    h = add(ses, torrent, save_dir)
    deadline = time.time() + 40
    last_dial = 0
    while True:
        st = h.status()
        if st.is_seeding:
            say('DONE %d' % st.total_payload_download)
            return 0
        if time.time() > deadline:
            say('FAIL timeout: state %s progress %.3f peers %d' % (st.state, st.progress, st.num_peers))
            return 1
        if st.num_peers == 0 and time.time() - last_dial > 1.0:
            h.connect_peer(('127.0.0.1', port))
            last_dial = time.time()
        time.sleep(0.05)


def main(argv):
    if len(argv) >= 4 and argv[1] == 'seed':
        return seed(argv[2], argv[3])
    if len(argv) >= 4 and argv[1] == 'seedenc':
        return seed(argv[2], argv[3], True)
    if len(argv) >= 5 and argv[1] == 'leech':
        return leech(argv[2], argv[3], int(argv[4]))
    say('FAIL usage')
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv))
