# MARSA Lab Gateway (untuk server Lubuntu)

Gerbang ini menerima sambungan KELUAR dari server guru di lab, lalu meneruskan
permintaan monitor dan kontrol dari aplikasi (desktop/Android) ke server guru itu.
Berkas dan tugas TIDAK diteruskan.

## Pasang
1. Ekstrak paket ke /opt/marsalab-gateway dan beri izin jalan:
       sudo mkdir -p /opt/marsalab-gateway
       sudo unzip MarsaLab-Gateway-linux.zip -d /opt/marsalab-gateway
       sudo chmod +x /opt/marsalab-gateway/MarsaLab.Gateway
2. Buat kunci lab dan hash sandi (jalankan untuk tiap pengguna; beri sandi minimal 10 karakter):
       /opt/marsalab-gateway/MarsaLab.Gateway newkey
       /opt/marsalab-gateway/MarsaLab.Gateway hash 'SandiGuruYangKuat'
3. Buat gateway.json dari contoh, isi kunci lab dan hash hasil langkah 2:
       cd /opt/marsalab-gateway && sudo cp gateway.example.json gateway.json
       sudo nano gateway.json
   Peran pengguna: "controller" (boleh kirim perintah dan kontrol layar) atau "viewer" (hanya memantau).
   ID lab memakai huruf kecil, angka, atau tanda hubung (contoh: lab1).
4. Buat pengguna layanan dan amankan file:
       sudo useradd --system --home /opt/marsalab-gateway --shell /usr/sbin/nologin marsalab
       sudo chown -R marsalab: /opt/marsalab-gateway
       sudo chmod 600 /opt/marsalab-gateway/gateway.json
5. Jalankan sebagai layanan:
       sudo cp /opt/marsalab-gateway/marsalab-gateway.service /etc/systemd/system/
       sudo systemctl daemon-reload
       sudo systemctl enable --now marsalab-gateway
       sudo systemctl status marsalab-gateway
6. Cloudflare Tunnel: tambahkan Public Hostname, mis. relay.marsa9.com -> HTTP -> localhost:5090
   (atau di config.yml cloudflared: ingress -> hostname: relay.marsa9.com, service: http://localhost:5090).
   WebSocket otomatis didukung.
7. Uji: buka https://relay.marsa9.com , login, lalu cek apakah lab tampil "online" setelah
   server guru dikonfigurasi (bagian Relay di appsettings.json server guru).

## Berkas penting
- gateway.json  : kunci lab + akun (hash). Ubah tanpa restart (dibaca ulang otomatis).
- audit.log     : catatan login, perintah, dan sesi kontrol (siapa, kapan, PC mana).
- Sesi login disimpan di memori: setelah layanan di-restart, semua pengguna harus login ulang.

## Catatan keamanan
- Gerbang hanya mendengarkan 127.0.0.1:5090; jangan buka portnya ke luar, biarkan lewat Cloudflare Tunnel.
- Login dikunci 5 menit setelah 5 kali salah dari IP yang sama.
- Pertimbangkan aturan rate limiting Cloudflare untuk /auth/login bila tersedia di paket Anda.
