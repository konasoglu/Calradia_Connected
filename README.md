# Calradia Connected (Beta)

[Türkçe](#turkce) | [English](#english)

---

<a id="turkce"></a>
## Türkçe

**Calradia Connected**, *Mount & Blade II: Bannerlord* için geliştirilen, oyuncuların aynı sefer haritasında birlikte oynayabilmesini sağlayan **beta aşamasındaki** bir çok oyunculu moddur.

Mod hâlâ geliştirme sürecindedir; temel sefer ve savaş sistemleri çalışmaktadır ancak zaman zaman hatalar veya eksik kısımlar olabilir.

### Şu An Neler Yapılabiliyor?
- Aynı Kalradya haritasında birden fazla oyuncuyla birlikte gezme, şehir/köylere girip asker toplama ve ticaret yapma
- Haritada veya şehirlerde (`Lordlarla İletişime Geç`) diğer oyuncularla 3D diyalog açma, altın/eşya/asker/gemi takası, ittifak, barış ve dostluk düellosu
- Oyuncular arası canlı savaşlar (iki tarafın da `HAZIRIM` demesiyle başlar), savaş sonrası esir alma ve idam
- İsteğe bağlı **War Sails (`NavalDLC`)** desteği (kurulu değilse normal şekilde çalışır)
- Masaüstü **Sunucu Yöneticisi** ile sunucu başlatma, zaman hızını yönetme, oyun kurallarını (savaş boyutu, zorluk, XP çarpanları, başlangıç eşyaları/askerleri, PvP/Arena) ayarlama, oyuncu hesaplarını ve admin araçlarını kullanma
- 8 dil desteği (`Türkçe, English, Polski, Deutsch, Русский, Français, Italiano, 简体中文`) ve otomatik güncelleme kontrolü

### Kurulum

#### Oyuncu (İstemci)
1. [Releases](https://github.com/konasoglu/Calradia_Connected/releases) sayfasından son `CalradiaConnected-Mod.zip` dosyasını indir.
2. İçindeki `CalradiaConnected` klasörünü oyun dizinine çıkar:
   ```text
   <Bannerlord Klasörü>\Modules\CalradiaConnected
   ```
3. Oyunu aç, mod listesinden **Calradia-Connected** modunu işaretle.
4. Ana menüde **Kalradya'ya Katıl** seçeneğine tıkla; sunucu IP'sini, portu (`4200`), kullanıcı adını ve şifreni girip bağlan.

#### Sunucu Kurmak İsteyenler
1. Önce yukarıdaki gibi `CalradiaConnected` modunu `Modules` klasörüne kur.
2. `CalradiaConnected-ServerManager.zip` dosyasını indirip istediğin bir klasöre çıkar.
3. `CalradiaConnected.ServerManager.exe` uygulamasını çalıştır (Windows x64 için [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) gerekir).
4. Oyun klasörünü kontrol et, ayarlarını yap ve **Sunucuyu Başlat** butonuna bas.
5. Aynı bilgisayardan oyuna girmek için **Bu Bilgisayarda Oyuna Katıl** butonunu kullanıp `127.0.0.1:4200` adresine bağlanabilirsin.

### Port ve Ağ Bilgisi
- **`4200 (UDP)`**: Oyun bağlantısı ve dünya senkronizasyonu (FastDL kapalıysa kayıt aktarımı da buradan yapılır).
- **`4201 (TCP)`**: Harita kaydı ve özel modların hızlı indirilmesi (FastDL).
- **`4205 (TCP - sadece yerel)`**: Sunucu Yöneticisi ile arka plandaki sunucu arasındaki bağlantı.

LAN, Radmin VPN veya Hamachi ile oynuyorsan Windows Güvenlik Duvarı'nda `4200/UDP` ve `4201/TCP` izinli olsun. İnternetten bağlanılacaksa modemden bu iki portu yönlendirmen gerekir.

### Oyun İçi Kısayollar ve Yönetici Komutları
- **`F9`**: Sefer haritasında sohbeti açar.
- **`Enter`**: Savaş ekranında sohbeti açar.

Admin yetkisi olan oyuncular `F9` sohbetinden şu komutları yazabilir:
- `/help` — Komut listesi
- `/gold <miktar> [oyuncu|all]` — Altın verir
- `/renown <miktar> [oyuncu|all]` — Nam verir
- `/influence <miktar> [oyuncu|all]` — Nüfuz verir
- `/heal [oyuncu|all]` — Kahramanı iyileştirir
- `/healparty [oyuncu|all]` — Orduyu iyileştirir
- `/troop <birlikId> <adet> [oyuncu|all]` — Asker ekler
- `/time <0|1|2>` — Zamanı durdurur (`0`), normal (`1`) veya hızlı (`2`) yapar
- `/save` — Dünyayı kaydeder
- `/kick <kullanıcı>` / `/ban <kullanıcı>` — Oyuncuyu atar veya yasaklar

### Kaynaktan Derleme
Windows x64, [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) ve kurulu Bannerlord gerekir:
```powershell
dotnet build CalradiaConnected.sln -c Release -p:Platform=x64
```
Bannerlord farklı bir diskteyse `-p:BannerlordGameDir="D:\Oyunlar\Mount & Blade II Bannerlord"` ekleyebilirsin.

### Sık Karşılaşılan Sorunlar
- **Steam API hatası:** Steam'in açık olduğundan ve Steam ile Sunucu Yöneticisi'nin aynı yetki düzeyinde (ikisi de normal veya ikisi de yönetici) çalıştığından emin ol.
- **Arkadaşlarım haritayı indiremiyor:** Sunucu bilgisayarında güvenlik duvarından `4200/UDP` ve `4201/TCP` portlarına izin ver.
- **War Sails (NavalDLC) ile RAM hatası:** War Sails açıksa Donanım sekmesinde RAM sınırını **Sınırsız / Otomatik** olarak bırak.

### Lisans
[MIT Lisansı](LICENSE) — Geliştirici: **konasoglu**.

---

<a id="english"></a>
## English

**Calradia Connected** is an open-source **beta** multiplayer campaign mod for *Mount & Blade II: Bannerlord* that lets friends play together on the same campaign map.

The mod is currently in beta — core campaign, interaction, and battle features are playable, though bugs and rough edges may still occur.

### What Works Right Now
- Shared Calradia campaign map with multiple players moving, visiting towns/villages, recruiting troops, and trading
- 3D player conversations on the map or inside settlements (`Contact Lords`) with direct gold/item/troop/ship barter, alliances, peace treaties, and friendly duels
- Synchronized player-vs-player battles (with a mutual `READY` start gate), post-battle capture, and hero execution
- Optional **War Sails (`NavalDLC`)** support (works normally whether the DLC is installed or not)
- Desktop **Server Manager** for starting/stopping the server, controlling map speed, tuning game rules (battle size, difficulty, XP multipliers, starter kits/troops, PvP/Arena), managing accounts, and using admin tools
- 8 languages (`English, Türkçe, Polski, Deutsch, Русский, Français, Italiano, 简体中文`) and automatic update checking

### Installation

#### Players (Client)
1. Download the latest `CalradiaConnected-Mod.zip` from [Releases](https://github.com/konasoglu/Calradia_Connected/releases).
2. Extract the `CalradiaConnected` folder into:
   ```text
   <Bannerlord Folder>\Modules\CalradiaConnected
   ```
3. Launch Bannerlord and enable **Calradia-Connected** in the module list.
4. Click **Join Calradia** in the main menu and enter the server IP, port (`4200`), username, and password.

#### Hosting a Server
1. Install the mod into `Modules\CalradiaConnected` as shown above.
2. Download and extract `CalradiaConnected-ServerManager.zip` anywhere.
3. Run `CalradiaConnected.ServerManager.exe` (requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows x64).
4. Check your Bannerlord path, configure your settings, and click **Start Server**.
5. To play on the same PC, click **Play on This PC** and connect to `127.0.0.1:4200`.

### Ports & Networking
- **`4200 (UDP)`**: Game connection, world sync, and UDP save transfer fallback.
- **`4201 (TCP)`**: FastDL HTTP world save and custom mod downloads.
- **`4205 (TCP - localhost only)`**: Local control link between Server Manager and the background server.

For LAN, Radmin VPN, or Hamachi, allow `4200/UDP` and `4201/TCP` in Windows Firewall. For direct Internet hosting, forward both ports on your router.

### In-Game Controls & Admin Chat Commands
- **`F9`**: Open global chat on the campaign map.
- **`Enter`**: Open chat during battles.

Players marked as Admin can run these commands in `F9` chat:
- `/help` — Show all commands
- `/gold <amount> [player|all]` — Add gold
- `/renown <amount> [player|all]` — Add clan renown
- `/influence <amount> [player|all]` — Add clan influence
- `/heal [player|all]` — Heal hero to full HP
- `/healparty [player|all]` — Heal hero and all wounded troops
- `/troop <troopId> <count> [player|all]` — Add troops by ID
- `/time <0|1|2>` — Pause (`0`), Normal (`1`), or Fast (`2`)
- `/save` — Save the world immediately
- `/kick <username>` / `/ban <username>` — Kick or ban a player

### Building from Source
Requires Windows x64, [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and Bannerlord:
```powershell
dotnet build CalradiaConnected.sln -c Release -p:Platform=x64
```
If your game is installed outside the default Steam folder, pass `-p:BannerlordGameDir="D:\Games\Mount & Blade II Bannerlord"`.

### Troubleshooting
- **Steam API error:** Make sure Steam is running and both Steam and Server Manager run at the same permission level (don't run only one as Administrator).
- **Friends can't download the save:** Allow `4200/UDP` and `4201/TCP` through Windows Firewall on the host PC.
- **War Sails memory crashes:** Leave **Memory Limit (RAM)** on *Unlimited / System Auto* when hosting with `NavalDLC` enabled.

### License
[MIT License](LICENSE) — Maintained by **konasoglu**.
