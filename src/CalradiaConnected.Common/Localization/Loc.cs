using System;
using System.Collections.Generic;
using System.Globalization;

namespace CalradiaConnected.Common.Localization
{
    /// <summary>
    /// Shared 8-language localization service (TR, EN, PL, DE, RU, FR, IT, ZH)
    /// used across both the Bannerlord Mod and the WPF Server Manager.
    /// Automatically syncs with Bannerlord's active game language or Windows OS culture.
    /// </summary>
    public static class Loc
    {
        public static readonly string[] SupportedLanguages = { "TR", "EN", "PL", "DE", "RU", "FR", "IT", "ZH" };

        private static string _currentLanguage = "EN";
        private static bool _isManuallyOverridden;

        public static string CurrentLanguage => _currentLanguage;
        public static bool IsManuallyOverridden => _isManuallyOverridden;
        public static bool IsTurkish => string.Equals(_currentLanguage, "TR", StringComparison.OrdinalIgnoreCase);
        public static bool IsEnglish => string.Equals(_currentLanguage, "EN", StringComparison.OrdinalIgnoreCase);

        public static event Action<string>? LanguageChanged;

        public static string GetNativeLanguageName(string? lang)
        {
            return Normalize(lang) switch
            {
                "TR" => "Türkçe",
                "EN" => "English",
                "PL" => "Polski",
                "DE" => "Deutsch",
                "RU" => "Русский",
                "FR" => "Français",
                "IT" => "Italiano",
                "ZH" => "简体中文",
                _ => "English"
            };
        }

        public static string GetShortLabel(string? lang) => Normalize(lang);

        public static void SetLanguage(string? lang)
        {
            string normalized = Normalize(lang);
            if (!string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _currentLanguage = normalized;
                LanguageChanged?.Invoke(_currentLanguage);
            }
        }

        public static void SetLanguageManually(string? lang)
        {
            _isManuallyOverridden = true;
            SetLanguage(lang);
        }

        public static void ToggleLanguage()
        {
            int idx = Array.IndexOf(SupportedLanguages, _currentLanguage);
            int next = (idx < 0 ? 0 : (idx + 1) % SupportedLanguages.Length);
            SetLanguageManually(SupportedLanguages[next]);
        }

        public static void SyncFromSystemCulture()
        {
            try
            {
                try { CultureInfo.CurrentUICulture?.ClearCachedData(); } catch { }

                string?[] candidates =
                {
                    CultureInfo.CurrentUICulture?.Name,
                    CultureInfo.CurrentUICulture?.TwoLetterISOLanguageName,
                    CultureInfo.InstalledUICulture?.Name,
                    CultureInfo.InstalledUICulture?.TwoLetterISOLanguageName,
                    CultureInfo.CurrentCulture?.Name,
                    CultureInfo.CurrentCulture?.TwoLetterISOLanguageName
                };

                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    string trimmed = candidate!.Trim();
                    if (string.Equals(trimmed, "iv", StringComparison.OrdinalIgnoreCase)) continue;
                    SetLanguage(MapIsoOrName(trimmed));
                    return;
                }

                SetLanguage("EN");
            }
            catch
            {
                SetLanguage("EN");
            }
        }

        public static void SyncFromBannerlordLanguage(string? bannerlordLanguage)
        {
            if (string.IsNullOrWhiteSpace(bannerlordLanguage)) return;
            SetLanguage(MapIsoOrName(bannerlordLanguage!));
        }

        private static string MapIsoOrName(string raw)
        {
            string s = raw.Trim();
            if (s.IndexOf("Türk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Turk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("tr-", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("tr_", StringComparison.OrdinalIgnoreCase))
                return "TR";

            if (s.IndexOf("Polsk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Polish", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Equals("pl", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("pl-", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("pl_", StringComparison.OrdinalIgnoreCase))
                return "PL";

            if (s.IndexOf("Deutsch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("German", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Equals("de", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("de-", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("de_", StringComparison.OrdinalIgnoreCase))
                return "DE";

            if (s.IndexOf("Русск", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Russ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("ru-", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("ru_", StringComparison.OrdinalIgnoreCase))
                return "RU";

            if (s.IndexOf("Franç", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Franc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("French", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Equals("fr", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("fr-", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("fr_", StringComparison.OrdinalIgnoreCase))
                return "FR";

            if (s.IndexOf("Ital", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.Equals("it", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("it-", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("it_", StringComparison.OrdinalIgnoreCase))
                return "IT";

            if (s.IndexOf("中文", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Chin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.StartsWith("cn", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return "ZH";

            return "EN";
        }

        public static string T(string turkish, string english)
        {
            if (IsTurkish) return turkish;
            if (IsEnglish) return english;
            return TranslateFromEnglish(english, _currentLanguage);
        }

        public static string F(string turkishFormat, string englishFormat, params object?[] args)
        {
            string fmt = T(turkishFormat, englishFormat);
            try
            {
                return string.Format(fmt, args);
            }
            catch
            {
                return string.Format(IsTurkish ? turkishFormat : englishFormat, args);
            }
        }

        private static string Normalize(string? lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "EN";
            return MapIsoOrName(lang!);
        }

        // Order in array: [0]=PL, [1]=DE, [2]=RU, [3]=FR, [4]=IT, [5]=ZH
        private static int GetExtraLangIndex(string lang) => lang switch
        {
            "PL" => 0,
            "DE" => 1,
            "RU" => 2,
            "FR" => 3,
            "IT" => 4,
            "ZH" => 5,
            _ => -1
        };

        private static string TranslateFromEnglish(string english, string lang)
        {
            int idx = GetExtraLangIndex(lang);
            if (idx < 0 || string.IsNullOrEmpty(english)) return english;
            if (ExtraTranslations.TryGetValue(english, out var arr) && idx < arr.Length && !string.IsNullOrEmpty(arr[idx]))
            {
                return arr[idx];
            }
            return english;
        }

        private static readonly Dictionary<string, string[]> ExtraTranslations =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // Main Menu & Login / Lobby / Credits
            ["Join Calradia"] = new[] { "Dołącz do Calradii", "Calradia beitreten", "Войти в Кальрадию", "Rejoindre Calradia", "Unisciti a Calradia", "加入卡拉迪亚" },
            ["JOIN CALRADIA"] = new[] { "DOŁĄCZ DO CALRADII", "CALRADIA BEITRETEN", "ВОЙТИ В КАЛЬРАДИЮ", "REJOINDRE CALRADIA", "UNISCITI A CALRADIA", "加入卡拉迪亚" },
            ["CALRADIA LOBBY (LAN & STEAM)"] = new[] { "LOBBY CALRADII (LAN I STEAM)", "CALRADIA-LOBBY (LAN & STEAM)", "ЛОББИ КАЛЬРАДИИ (LAN И STEAM)", "SALON CALRADIA (LAN & STEAM)", "LOBBY DI CALRADIA (LAN E STEAM)", "卡拉迪亚大厅（局域网与 Steam）" },
            ["▶ Direct Connect"] = new[] { "▶ Połącz bezpośrednio", "▶ Direkt verbinden", "▶ Прямое подключение", "▶ Connexion directe", "▶ Connessione diretta", "▶ 直接连接" },
            ["Direct Connect"] = new[] { "Połącz bezpośrednio", "Direkt verbinden", "Прямое подключение", "Connexion directe", "Connessione diretta", "直接连接" },
            ["▶ Lobby (LAN / Steam)"] = new[] { "▶ Lobby (LAN / Steam)", "▶ Lobby (LAN / Steam)", "▶ Лобби (LAN / Steam)", "▶ Salon (LAN / Steam)", "▶ Lobby (LAN / Steam)", "▶ 大厅（局域网 / Steam）" },
            ["Lobby (LAN / Steam)"] = new[] { "Lobby (LAN / Steam)", "Lobby (LAN / Steam)", "Лобби (LAN / Steam)", "Salon (LAN / Steam)", "Lobby (LAN / Steam)", "大厅（局域网 / Steam）" },
            ["Server IP Address:"] = new[] { "Adres IP serwera:", "Server-IP-Adresse:", "IP-адрес сервера:", "Adresse IP du serveur :", "Indirizzo IP del server:", "服务器 IP 地址：" },
            ["Port:"] = new[] { "Port:", "Port:", "Порт:", "Port :", "Porta:", "端口：" },
            ["Username:"] = new[] { "Nazwa użytkownika:", "Benutzername:", "Имя пользователя:", "Nom d'utilisateur :", "Nome utente:", "用户名：" },
            ["Account Password:"] = new[] { "Hasło konta:", "Kontopasswort:", "Пароль аккаунта:", "Mot de passe du compte :", "Password account:", "账号密码：" },
            ["Server Password (Optional):"] = new[] { "Hasło serwera (opcjonalne):", "Serverpasswort (optional):", "Пароль сервера (необязательно):", "Mot de passe du serveur (optionnel) :", "Password server (opzionale):", "服务器密码（可选）：" },
            ["Cancel"] = new[] { "Anuluj", "Abbrechen", "Отмена", "Annuler", "Annulla", "取消" },
            ["Connect"] = new[] { "Połącz", "Verbinden", "Подключиться", "Se connecter", "Connetti", "连接" },
            ["Rescan Servers"] = new[] { "Szukaj ponownie", "Server neu suchen", "Обновить список", "Rechercher à nouveau", "Cerca di nuovo", "重新扫描服务器" },
            ["Back to Login"] = new[] { "Powrót do logowania", "Zurück zum Login", "Назад ко входу", "Retour à la connexion", "Torna all'accesso", "返回登录" },
            ["Close"] = new[] { "Zamknij", "Schließen", "Закрыть", "Fermer", "Chiudi", "关闭" },
            ["Credits"] = new[] { "Twórcy", "Mitwirkende", "Авторы", "Crédits", "Crediti", "制作人员" },
            ["Developer"] = new[] { "Twórca", "Entwickler", "Разработчик", "Développeur", "Sviluppatore", "开发者" },
            ["Developer: konasoglu"] = new[] { "Twórca: konasoglu", "Entwickler: konasoglu", "Разработчик: konasoglu", "Développeur : konasoglu", "Sviluppatore: konasoglu", "开发者：konasoglu" },
            ["[X] Remember Me (On)"] = new[] { "[X] Zapamiętaj mnie (Wł.)", "[X] Angemeldet bleiben (Ein)", "[X] Запомнить меня (Вкл.)", "[X] Se souvenir de moi (Oui)", "[X] Ricordami (Sì)", "[X] 记住我（已开启）" },
            ["[  ] Remember Me (Off)"] = new[] { "[  ] Zapamiętaj mnie (Wył.)", "[  ] Angemeldet bleiben (Aus)", "[  ] Запомнить меня (Выкл.)", "[  ] Se souvenir de moi (Non)", "[  ] Ricordami (No)", "[  ] 记住我（已关闭）" },
            ["Enter your account password (a new account is created on first login)."] = new[] { "Wpisz hasło konta (przy pierwszym logowaniu konto utworzy się automatycznie).", "Gib dein Kontopasswort ein (beim ersten Login wird automatisch ein Konto erstellt).", "Введите пароль аккаунта (при первом входе аккаунт создаётся автоматически).", "Saisissez votre mot de passe (un compte est créé lors de votre première connexion).", "Inserisci la password dell'account (al primo accesso viene creato automaticamente).", "请输入账号密码（首次登录将自动创建新账号）。" },
            ["Scanning LAN and Steam servers..."] = new[] { "Wyszukiwanie serwerów w sieci LAN i Steam...", "Suche nach LAN- und Steam-Servern...", "Поиск серверов в LAN и Steam...", "Recherche des serveurs LAN et Steam...", "Ricerca server LAN e Steam in corso...", "正在搜索局域网和 Steam 服务器..." },
            ["[LOCKED]"] = new[] { "[ZAMKNIĘTY]", "[GESCHÜTZT]", "[ПАРОЛЬ]", "[PROTÉGÉ]", "[PROTETTO]", "[加密]" },
            ["[OPEN]"] = new[] { "[OTWARTY]", "[OFFEN]", "[ОТКРЫТ]", "[OUVERT]", "[APERTO]", "[开放]" },
            ["Players"] = new[] { "Gracze", "Spieler", "Игроки", "Joueurs", "Giocatori", "玩家" },
            ["Player"] = new[] { "Gracz", "Spieler", "Игрок", "Joueur", "Giocatore", "玩家" },
            ["Player #{0}"] = new[] { "Gracz #{0}", "Spieler #{0}", "Игрок #{0}", "Joueur #{0}", "Giocatore #{0}", "玩家 #{0}" },

            // Settlement & Lord Contact Menus
            ["Contact Lords"] = new[] { "Porozmawiaj z lordami", "Mit Fürsten sprechen", "Связаться с лордами", "Contacter les seigneurs", "Contatta i lord", "联系领主" },
            ["{0} other lord(s) are inside this settlement. Click to speak, trade, or duel."] = new[] { "W tej osadzie przebywa {0} innych lordów. Kliknij, aby porozmawiać, handlować lub stoczyć pojedynek.", "{0} weitere Fürsten befinden sich in dieser Siedlung. Klicke zum Sprechen, Handeln oder Duellieren.", "В этом поселении находится ещё {0} лорд(ов). Нажмите, чтобы поговорить, поторговать или вызвать на дуэль.", "{0} autre(s) seigneur(s) se trouve(nt) dans cette colonie. Cliquez pour parler, commercer ou vous battre en duel.", "Ci sono {0} altri lord in questo insediamento. Clicca per parlare, commerciare o duellare.", "此定居点内还有 {0} 位其他领主。点击进行交谈、交易或决斗。" },
            ["Speak with other lords across the realm."] = new[] { "Skontaktuj się z innymi lordami w krainie.", "Sprich mit anderen Fürsten im Reich.", "Свяжитесь с другими лордами в землях Кальрадии.", "Parlez avec d'autres seigneurs du royaume.", "Parla con gli altri lord del regno.", "与王国中的其他领主联系。" },

            // Humanized 3D Conversation & Diplomacy
            ["Greetings, my friend."] = new[] { "Witaj, przyjacielu.", "Seid gegrüßt, mein Freund.", "Приветствую тебя, друг.", "Salutations, mon ami.", "Salute a te, amico mio.", "向你致敬，我的朋友。" },
            ["Trade"] = new[] { "Handel", "Handeln", "Торговать", "Commerce", "Commercio", "交易" },
            ["Declare War"] = new[] { "Wypowiedz wojnę", "Krieg erklären", "Объявить войну", "Déclarer la guerre", "Dichiara guerra", "宣战" },
            ["Offer Peace"] = new[] { "Zaproponuj pokój", "Frieden anbieten", "Предложить мир", "Proposer la paix", "Proponi la pace", "提议和平" },
            ["Diplomacy & Relations"] = new[] { "Dyplomacja i relacje", "Diplomatie & Beziehungen", "Дипломатия и отношения", "Diplomatie et relations", "Diplomazia e relazioni", "外交与关系" },
            ["Speak"] = new[] { "Powiedz coś", "Sprechen", "Сказать", "Parler", "Parla", "交谈" },
            ["End Conversation"] = new[] { "Zakończ rozmowę", "Gespräch beenden", "Завершить разговор", "Mettre fin à la conversation", "Termina conversazione", "结束对话" },
            ["Leave"] = new[] { "Odejdź", "Verlassen", "Уйти", "Partir", "Esci", "离开" },
            ["Back"] = new[] { "Wróć", "Zurück", "Назад", "Retour", "Indietro", "返回" },
            ["Back to Conversation"] = new[] { "Wróć do rozmowy", "Zurück zum Gespräch", "Вернуться к разговору", "Retour à la conversation", "Torna alla conversazione", "返回交谈" },
            ["I accept your offer."] = new[] { "Przyjmuję twoją ofertę.", "Ich nehme Euer Angebot an.", "Я принимаю твоё предложение.", "J'accepte votre offre.", "Accetto la tua proposta.", "我接受你的提议。" },
            ["I must decline your offer."] = new[] { "Muszę odmówić.", "Ich muss ablehnen.", "Я вынужден отказаться.", "Je dois décliner.", "Devo rifiutare.", "我只能婉拒。" },
            ["Offer a Gift (500 Gold)"] = new[] { "Wręcz dar (500 złota)", "Geschenk anbieten (500 Gold)", "Преподнести дар (500 золота)", "Offrir un présent (500 or)", "Offri un dono (500 oro)", "赠送礼物（500 第纳尔）" },
            ["Propose an Alliance"] = new[] { "Zaproponuj sojusz", "Bündnis vorschlagen", "Предложить союз", "Proposer une alliance", "Proponi un'alleanza", "提议结盟" },
            ["Challenge to a Friendly Duel"] = new[] { "Wyzwij na towarzyski pojedynek", "Zum Freundschaftsduell fordern", "Вызвать на дружескую дуэль", "Proposer un duel amical", "Sfida a duello amichevole", "发起友谊决斗" },
            ["Call to Arms"] = new[] { "Wezwij na wspólną wyprawę", "Gemeinsamen Feldzug ausrufen", "Созвать в общий поход", "Appeler à une campagne commune", "Chiama alle armi", "号召共同出征" },
            ["Sworn Ally"] = new[] { "Wierny sojusznik", "Treuer Verbündeter", "Верный союзник", "Allié juré", "Alleato fedele", "铁杆盟友" },
            ["Friendly"] = new[] { "Przyjazny", "Freundlich", "Дружелюбный", "Amical", "Amichevole", "友好" },
            ["Bitter Enemy"] = new[] { "Zacięty wróg", "Erzfeind", "Заклятый враг", "Ennemi juré", "Nemico giurato", "死敌" },
            ["Hostile"] = new[] { "Wrogi", "Feindselig", "Враждебный", "Hostile", "Ostile", "敌对" },
            ["Neutral"] = new[] { "Neutralny", "Neutral", "Нейтральный", "Neutre", "Neutrale", "中立" },
            ["At War"] = new[] { "W stanie wojny", "Im Krieg", "В состоянии войны", "En guerre", "In guerra", "交战中" },
            ["Allied"] = new[] { "W sojuszu", "Verbündet", "Союзники", "Alliés", "Alleati", "已结盟" },
            ["At Peace"] = new[] { "W pokoju", "Im Frieden", "В мире", "En paix", "In pace", "和平中" },

            // Trade Table
            ["Trade with {0}"] = new[] { "Handel z {0}", "Handel mit {0}", "Торговля с {0}", "Commerce avec {0}", "Commercio con {0}", "与 {0} 交易" },
            ["Trade completes once both sides accept."] = new[] { "Wymiana zakończy się, gdy obie strony wyrażą zgodę.", "Der Handel wird abgeschlossen, sobald beide Seiten zustimmen.", "Обмен состоится, как только обе стороны подтвердят сделку.", "L'échange s'effectue une fois que les deux parties acceptent.", "Lo scambio si conclude quando entrambe le parti accettano.", "双方均确认后即可完成交易。" },
            ["Trade Table"] = new[] { "Stół handlowy", "Handelstisch", "Стол обмена", "Table d'échange", "Tavolo di scambio", "交易台" },
            ["Place your offers below; both sides must accept to complete."] = new[] { "Złóż ofertę poniżej; obie strony muszą potwierdzić wymianę.", "Lege unten dein Angebot ab; beide Seiten müssen annehmen.", "Разместите предложение ниже; обе стороны должны подтвердить.", "Placez vos offres ci-dessous ; les deux parties doivent accepter.", "Inserisci la tua offerta qui sotto; entrambe le parti devono accettare.", "请在下方放入报价，双方确认后即可成交。" },
            ["Accept Trade"] = new[] { "Zatwierdź handel", "Handel annehmen", "Подтвердить обмен", "Accepter l'échange", "Accetta scambio", "确认交易" },
            ["Withdraw Accept"] = new[] { "Cofnij zgodę", "Zustimmung zurückziehen", "Отменить согласие", "Retirer l'accord", "Ritira conferma", "撤回确认" },
            ["Clear Trade"] = new[] { "Wyczyść ofertę", "Angebot leeren", "Очистить стол", "Effacer l'offre", "Svuota offerta", "清空报价" },
            ["Accept Offer"] = new[] { "Przyjmij ofertę", "Angebot annehmen", "Принять предложение", "Accepter l'offre", "Accetta offerta", "接受提议" },
            ["Accept Rally"] = new[] { "Dołącz do wyprawy", "Feldzug beitreten", "Присоединиться к походу", "Rejoindre la campagne", "Unisciti alla campagna", "响应出征" },
            ["Decline"] = new[] { "Odrzuć", "Ablehnen", "Отклонить", "Refuser", "Rifiuta", "拒绝" },
            ["Offer Gold"] = new[] { "Zaproponuj złoto", "Gold anbieten", "Предложить золото", "Offrir de l'or", "Offri oro", "提供金币" },
            ["Offer Troops"] = new[] { "Zaproponuj żołnierzy", "Truppen anbieten", "Предложить воинов", "Offrir des troupes", "Offri truppe", "提供士兵" },
            ["Offer Item"] = new[] { "Zaproponuj przedmiot", "Gegenstand anbieten", "Предложить предмет", "Offrir un objet", "Offri oggetto", "提供物品" },
            ["Offer Ship"] = new[] { "Zaproponuj statek", "Schiff anbieten", "Предложить корабль", "Offrir un navire", "Offri nave", "提供船只" },
            ["1. Gold"] = new[] { "1. Złoto", "1. Gold", "1. Золото", "1. Or", "1. Oro", "1. 金币" },
            ["2. Troops"] = new[] { "2. Żołnierze", "2. Truppen", "2. Воины", "2. Troupes", "2. Truppe", "2. 士兵" },
            ["3. Item"] = new[] { "3. Przedmiot", "3. Gegenstand", "3. Предмет", "3. Objet", "3. Oggetto", "3. 物品" },
            ["4. Ship"] = new[] { "4. Statek", "4. Schiff", "4. Корабль", "4. Navire", "4. Nave", "4. 船只" },
            ["Friendly Duel"] = new[] { "Towarzyski pojedynek", "Freundschaftsduell", "Дружеская дуэль", "Duel amical", "Duello amichevole", "友谊决斗" },
            ["Call Joint Rally"] = new[] { "Wspólna wyprawa", "Gemeinsamer Feldzug", "Общий поход", "Campagne commune", "Campagna comune", "共同出征" },
            ["Speak Line"] = new[] { "Powiedz coś", "Nachricht senden", "Сказать слово", "Envoyer un message", "Invia messaggio", "发言" },
            ["No offer"] = new[] { "Brak oferty", "Kein Angebot", "Нет предложения", "Aucune offre", "Nessuna offerta", "暂无报价" },
            ["[ACCEPTED ✓]"] = new[] { "[ZATWIERDZONO ✓]", "[BESTÄTIGT ✓]", "[ПОДТВЕРЖДЕНО ✓]", "[ACCEPTÉ ✓]", "[ACCETTATO ✓]", "[已确认 ✓]" },
            ["[Waiting]"] = new[] { "[Oczekuje]", "[Wartet]", "[Ожидание]", "[En attente]", "[In attesa]", "[等待中]" },
            ["{0} Gold"] = new[] { "{0} złota", "{0} Gold", "{0} золота", "{0} or", "{0} oro", "{0} 第纳尔" },

            // Battle Ready, Combat & Capture
            ["Opponent"] = new[] { "Przeciwnik", "Gegner", "Противник", "Adversaire", "Avversario", "对手" },
            ["Battle Preparation — {0}"] = new[] { "Przygotowanie do bitwy — {0}", "Schlachtvorbereitung — {0}", "Подготовка к бою — {0}", "Préparation au combat — {0}", "Preparazione alla battaglia — {0}", "战前准备 — {0}" },
            ["READY"] = new[] { "GOTÓW", "BEREIT", "ГОТОВ", "PRÊT", "PRONTO", "准备就绪" },
            ["READY ✓"] = new[] { "GOTÓW ✓", "BEREIT ✓", "ГОТОВ ✓", "PRÊT ✓", "PRONTO ✓", "已准备 ✓" },
            ["Waiting..."] = new[] { "Oczekiwanie...", "Wartet...", "Ожидание...", "En attente...", "In attesa...", "等待中..." },
            ["You: {0}   |   {1}: {2}"] = new[] { "Ty: {0}   |   {1}: {2}", "Du: {0}   |   {1}: {2}", "Вы: {0}   |   {1}: {2}", "Vous : {0}   |   {1} : {2}", "Tu: {0}   |   {1}: {2}", "你：{0}   |   {1}：{2}" },
            ["READY CONFIRMED ✓ — Waiting for opponent..."] = new[] { "GOTOWOŚĆ POTWIERDZONA ✓ — Oczekiwanie na przeciwnika...", "BEREIT BESTÄTIGT ✓ — Warte auf den Gegner...", "ГОТОВНОСТЬ ПОДТВЕРЖДЕНА ✓ — Ожидаем противника...", "PRÊT CONFIRMÉ ✓ — En attente de l'adversaire...", "PRONTO CONFERMATO ✓ — In attesa dell'avversario...", "已确认准备 ✓ — 正在等待对手..." },
            ["Both sides are READY! Battle begins!"] = new[] { "Obie strony są GOTOWE! Bitwa się zaczyna!", "Beide Seiten sind BEREIT! Die Schlacht beginnt!", "Обе стороны ГОТОВЫ! Бой начинается!", "Les deux camps sont PRÊTS ! La bataille commence !", "Entrambe le parti sono PRONTE! La battaglia inizia!", "双方准备就绪！战斗开始！" },
            ["READY confirmed! Waiting for opponent..."] = new[] { "Gotowość wysłana! Oczekiwanie na przeciwnika...", "Bereitschaft gesendet! Warte auf den Gegner...", "Готовность отправлена! Ожидаем противника...", "Confirmation envoyée ! En attente de l'adversaire...", "Conferma inviata! In attesa dell'avversario...", "已发送准备信号！正在等待对手..." },
            ["Attack blocked with shield!"] = new[] { "Atak zablokowany tarczą!", "Angriff mit dem Schild abgewehrt!", "Удар отражён щитом!", "Attaque bloquée au bouclier !", "Attacco bloccato con lo scudo!", "已用盾牌格挡攻击！" },
            ["'{0}' has been taken prisoner and added to your party!"] = new[] { "'{0}' trafił do niewoli w twoim oddziale!", "'{0}' wurde gefangen genommen und deiner Truppe hinzugefügt!", "'{0}' взят в плен и добавлен в ваш отряд!", "'{0}' a été fait prisonnier et ajouté à votre troupe !", "'{0}' è stato catturato e aggiunto al tuo gruppo!", "'{0}' 已被俘虏并关押在你的部队中！" },
            ["You were taken prisoner by '{0}'!"] = new[] { "'{0}' wziął cię do niewoli!", "Du wurdest von '{0}' gefangen genommen!", "Вас взял в плен '{0}'!", "Vous avez été fait prisonnier par '{0}' !", "Sei stato catturato da '{0}'!", "你已被 '{0}' 俘虏！" },
            ["Your Hero Has Been Executed!"] = new[] { "Twój bohater został stracony!", "Dein Held wurde hingerichtet!", "Ваш герой казнён!", "Votre héros a été exécuté !", "Il tuo eroe è stato giustiziato!", "你的英雄已被处决！" },
            ["Your Hero Has Fallen Permanently!"] = new[] { "Twój bohater poległ na zawsze!", "Dein Held ist dauerhaft gefallen!", "Ваш герой пал навсегда!", "Votre héros est tombé définitivement !", "Il tuo eroe è caduto per sempre!", "你的英雄已永久阵亡！" },
            ["Create New Character"] = new[] { "Stwórz nową postać", "Neuen Charakter erstellen", "Создать нового персонажа", "Créer un nouveau personnage", "Crea nuovo personaggio", "创建新角色" },
            ["Disconnect"] = new[] { "Rozłącz się", "Verbindung trennen", "Выйти с сервера", "Se déconnecter", "Disconnetti", "离开服务器" },
            ["Battlefield Chat"] = new[] { "Czat bitewny", "Schlachtfeld-Chat", "Боевой чат", "Chat de bataille", "Chat di battaglia", "战场聊天" },
            ["Send"] = new[] { "Wyślij", "Senden", "Отправить", "Envoyer", "Invia", "发送" },
            ["Battle"] = new[] { "Bitwa", "Schlacht", "Бой", "Bataille", "Battaglia", "战斗" },
            ["Chat"] = new[] { "Czat", "Chat", "Чат", "Chat", "Chat", "聊天" },
            ["Whisper"] = new[] { "Szept", "Flüstern", "Шёпот", "Chuchoter", "Sussurro", "私聊" },

            // Server Manager Dynamic Status, Troops & All Dropdown Options (PL, DE, RU, FR, IT, ZH)
            ["ONLINE"] = new[] { "ONLINE", "ONLINE", "В СЕТИ", "EN LIGNE", "ONLINE", "在线" },
            ["OFFLINE"] = new[] { "OFFLINE", "OFFLINE", "НЕ В СЕТИ", "HORS LIGNE", "OFFLINE", "离线" },
            ["WORKING..."] = new[] { "PRACUJE...", "ARBEITET...", "ОБРАБОТКА...", "EN COURS...", "ELABORAZIONE...", "处理中..." },
            ["Server Offline"] = new[] { "Serwer wyłączony", "Server offline", "Сервер выключен", "Serveur hors ligne", "Server offline", "服务器离线" },
            ["Server is ready."] = new[] { "Serwer jest gotowy.", "Server ist bereit.", "Сервер готов к работе.", "Le serveur est prêt.", "Il server è pronto.", "服务器已就绪。" },
            ["Server running (Port: {0})."] = new[] { "Serwer działa (Port: {0}).", "Server läuft (Port: {0}).", "Сервер работает (Порт: {0}).", "Serveur actif (Port : {0}).", "Server attivo (Porta: {0}).", "服务器运行中（端口：{0}）。" },
            ["Server stopped."] = new[] { "Serwer zatrzymany.", "Server gestoppt.", "Сервер остановлен.", "Serveur arrêté.", "Server fermato.", "服务器已停止。" },
            ["Unlimited"] = new[] { "Bez limitu", "Unbegrenzt", "Без лимита", "Illimité", "Illimitato", "无限制" },
            ["Unknown"] = new[] { "Nieznany", "Unbekannt", "Неизвестно", "Inconnu", "Sconosciuto", "未知" },
            ["New Game"] = new[] { "Nowa gra", "Neues Spiel", "Новая игра", "Nouvelle partie", "Nuova partita", "新游戏" },
            ["Wounded"] = new[] { "Ranni", "Verwundet", "Ранены", "Blessés", "Feriti", "伤兵" },
            ["Registered"] = new[] { "Zarejestrowany", "Registriert", "Зарегистрирован", "Inscrit", "Registrato", "已注册" },
            ["Cores"] = new[] { "Rdzenie", "Kerne", "Ядер", "Cœurs", "Core", "核心" },
            ["Paused (0x)"] = new[] { "Pauza (0x)", "Pausiert (0x)", "Пауза (0x)", "Pause (0x)", "In pausa (0x)", "已暂停 (0x)" },
            ["Normal (1.0x)"] = new[] { "Normalnie (1.0x)", "Normal (1,0x)", "Обычно (1.0x)", "Normal (1.0x)", "Normale (1.0x)", "正常 (1.0x)" },
            ["Fast (2.0x)"] = new[] { "Szybko (2.0x)", "Schnell (2,0x)", "Быстро (2.0x)", "Rapide (2.0x)", "Veloce (2.0x)", "快速 (2.0x)" },
            ["Very Fast (3.0x)"] = new[] { "Bardzo szybko (3.0x)", "Sehr schnell (3,0x)", "Очень быстро (3.0x)", "Très rapide (3.0x)", "Molto veloce (3.0x)", "极速 (3.0x)" },
            ["Enter a server IP address and check server mods."] = new[] { "Wpisz adres IP serwera i sprawdź mody.", "Gib die Server-IP ein und prüfe die Mods.", "Введите IP сервера и проверьте моды.", "Entrez l'IP du serveur et vérifiez les mods.", "Inserisci l'IP del server e controlla le mod.", "输入服务器 IP 并检查所需模组。" },

            // Troop Names, Cultures & Tiers
            ["Imperial Elite Cataphract"] = new[] { "Elitarny katafrakt Cesarstwa", "Kaiserlicher Elite-Kataphrakt", "Имперский элитный катафрактарий", "Cataphractaire d'élite impérial", "Catafratto d'élite imperiale", "帝国精锐具装骑兵" },
            ["Vlandian Banner Knight"] = new[] { "Vlandyjski rycerz chorągwiany", "Vlandianischer Bannerritter", "Вландийский рыцарь-знаменосец", "Chevalier banneret vlandien", "Cavaliere vessillifero vlandiano", "瓦兰迪亚方旗骑士" },
            ["Battanian Fian Champion"] = new[] { "Battański czempion Fian", "Battanischer Fian-Champion", "Баттанский фиан-чемпион", "Champion Fian battanien", "Campione Fian battaniano", "巴旦尼亚菲奥纳冠军" },
            ["Khuzait Khan's Guard"] = new[] { "Gwardzista chana Khuzaitów", "Khuzaitscher Khangardist", "Ханская стража хузаитов", "Garde du Khan khuzait", "Guardia del Khan khuzait", "库赛特可汗亲卫" },
            ["Sturgian Heavy Axeman"] = new[] { "Sturgijski ciężki topornik", "Sturgischer schwerer Axtkämpfer", "Стургийский тяжёлый топорщик", "Hacheur lourd sturgien", "Asciere pesante sturgiano", "斯特吉亚重装斧兵" },
            ["Sturgian Druzhinnik Champion"] = new[] { "Sturgijski czempion drużynnik", "Sturgischer Druschinnik-Champion", "Стургийский дружинник-чемпион", "Champion droujinnik sturgien", "Campione Druzhinnik sturgiano", "斯特吉亚亲卫冠军" },
            ["Aserai Vanguard Faris"] = new[] { "Aserajski faris straży przedniej", "Aserai-Vorhut-Faris", "Асерайский авангардный фарис", "Faris d'avant-garde aseraï", "Faris d'avanguardia aserai", "阿塞莱先锋法里斯" },
            ["Imperial Legionary"] = new[] { "Legionista Cesarstwa", "Kaiserlicher Legionär", "Имперский легионер", "Légionnaire impérial", "Legionario imperiale", "帝国军团步兵" },
            ["Vlandian Sharpshooter"] = new[] { "Vlandyjski strzelec wyborowy", "Vlandianischer Scharfschütze", "Вландийский снайпер", "Tireur d'élite vlandien", "Tiratore scelto vlandiano", "瓦兰迪亚神射手" },
            ["Aserai Mameluke Heavy Cavalry"] = new[] { "Ciężka kawaleria mameluków Aserai", "Schwere Aserai-Mamluken-Kavallerie", "Асерайская тяжёлая конница мамелюков", "Cavalerie lourde mamelouke aseraï", "Cavalleria pesante mamelucca aserai", "阿塞莱马穆鲁克重骑兵" },
            ["Empire"] = new[] { "Cesarstwo", "Kaiserreich", "Империя", "Empire", "Impero", "帝国" },
            ["Vlandia"] = new[] { "Vlandia", "Vlandia", "Вландия", "Vlandia", "Vlandia", "瓦兰迪亚" },
            ["Battania"] = new[] { "Battania", "Battania", "Баттания", "Battania", "Battania", "巴旦尼亚" },
            ["Khuzait"] = new[] { "Khuzait", "Khuzait", "Хузаит", "Khuzait", "Khuzait", "库赛特" },
            ["Sturgia"] = new[] { "Sturgia", "Sturgia", "Стургия", "Sturgia", "Sturgia", "斯特吉亚" },
            ["Aserai"] = new[] { "Aserai", "Aserai", "Асерай", "Aseraï", "Aserai", "阿塞莱" },
            ["Tier 5"] = new[] { "Poziom 5", "Stufe 5", "Уровень 5", "Rang 5", "Grado 5", "5 级" },
            ["Tier 6"] = new[] { "Poziom 6", "Stufe 6", "Уровень 6", "Rang 6", "Grado 6", "6 级" },

            // Hardware & Performance Dropdown Options
            ["Unlimited / System Auto (Recommended)"] = new[] { "Bez limitu / Auto (Zalecane)", "Unbegrenzt / System-Auto (Empfohlen)", "Без лимита / Авто (Рекомендуется)", "Illimité / Auto système (Recommandé)", "Illimitato / Auto (Consigliato)", "无限制 / 系统自动（推荐）" },
            ["4 GB RAM Limit (Low Spec)"] = new[] { "Limit 4 GB RAM (Słabszy sprzęt)", "4 GB RAM-Limit (Niedrig)", "Лимит 4 ГБ ОЗУ (Слабый ПК)", "Limite 4 Go RAM (Config légère)", "Limite 4 GB RAM (Basso)", "4 GB 内存上限（低配）" },
            ["6 GB RAM Limit"] = new[] { "Limit 6 GB RAM", "6 GB RAM-Limit", "Лимит 6 ГБ ОЗУ", "Limite 6 Go RAM", "Limite 6 GB RAM", "6 GB 内存上限" },
            ["8 GB RAM Limit (Balanced)"] = new[] { "Limit 8 GB RAM (Zbalansowany)", "8 GB RAM-Limit (Ausgewogen)", "Лимит 8 ГБ ОЗУ (Сбалансированный)", "Limite 8 Go RAM (Équilibré)", "Limite 8 GB RAM (Bilanciato)", "8 GB 内存上限（平衡）" },
            ["12 GB RAM Limit (Large Campaign)"] = new[] { "Limit 12 GB RAM (Duża kampania)", "12 GB RAM-Limit (Große Kampagne)", "Лимит 12 ГБ ОЗУ (Большая кампания)", "Limite 12 Go RAM (Grande campagne)", "Limite 12 GB RAM (Grande campagna)", "12 GB 内存上限（大型战役）" },
            ["16 GB RAM Limit (High Performance)"] = new[] { "Limit 16 GB RAM (Wysoka wydajność)", "16 GB RAM-Limit (Hohe Leistung)", "Лимит 16 ГБ ОЗУ (Высокая производительность)", "Limite 16 Go RAM (Haute performance)", "Limite 16 GB RAM (Alte prestazioni)", "16 GB 内存上限（高性能）" },
            ["24 GB RAM Limit"] = new[] { "Limit 24 GB RAM", "24 GB RAM-Limit", "Лимит 24 ГБ ОЗУ", "Limite 24 Go RAM", "Limite 24 GB RAM", "24 GB 内存上限" },
            ["32 GB RAM Limit (Max)"] = new[] { "Limit 32 GB RAM (Maks.)", "32 GB RAM-Limit (Max.)", "Лимит 32 ГБ ОЗУ (Макс.)", "Limite 32 Go RAM (Max)", "Limite 32 GB RAM (Massimo)", "32 GB 内存上限（最大）" },

            ["Ultra Low (512 MB - 1 GB VRAM)"] = new[] { "Bardzo niski (512 MB - 1 GB VRAM)", "Ultra niedrig (512 MB - 1 GB VRAM)", "Сверхнизкий (512 МБ - 1 ГБ VRAM)", "Très bas (512 Mo - 1 Go VRAM)", "Ultra basso (512 MB - 1 GB VRAM)", "极低（512 MB - 1 GB 显存）" },
            ["Low (1 GB - 2 GB VRAM)"] = new[] { "Niski (1 GB - 2 GB VRAM)", "Niedrig (1 GB - 2 GB VRAM)", "Низкий (1 ГБ - 2 ГБ VRAM)", "Bas (1 Go - 2 Go VRAM)", "Basso (1 GB - 2 GB VRAM)", "低（1 GB - 2 GB 显存）" },
            ["Medium (2 GB - 4 GB VRAM - Recommended)"] = new[] { "Średni (2 GB - 4 GB VRAM - Zalecane)", "Mittel (2 GB - 4 GB VRAM - Empfohlen)", "Средний (2 ГБ - 4 ГБ VRAM - Рекомендуется)", "Moyen (2 Go - 4 Go VRAM - Recommandé)", "Medio (2 GB - 4 GB VRAM - Consigliato)", "中（2 GB - 4 GB 显存 - 推荐）" },
            ["High (4 GB - 6 GB VRAM)"] = new[] { "Wysoki (4 GB - 6 GB VRAM)", "Hoch (4 GB - 6 GB VRAM)", "Высокий (4 ГБ - 6 ГБ VRAM)", "Élevé (4 Go - 6 Go VRAM)", "Alto (4 GB - 6 GB VRAM)", "高（4 GB - 6 GB 显存）" },
            ["Max (8+ GB VRAM / Full Textures)"] = new[] { "Maksymalny (8+ GB VRAM)", "Maximal (8+ GB VRAM / Volle Texturen)", "Максимум (8+ ГБ VRAM)", "Maximum (8+ Go VRAM)", "Massimo (8+ GB VRAM)", "最高（8+ GB 显存 / 完整纹理）" },

            ["20 FPS (Ultra Light / Minimum PC Load - Recommended)"] = new[] { "20 FPS (Najmniejsze obciążenie - Zalecane)", "20 FPS (Ultraleicht / Minimale Last - Empfohlen)", "20 FPS (Минимальная нагрузка - Рекомендуется)", "20 FPS (Ultra léger - Recommandé)", "20 FPS (Leggerissimo - Consigliato)", "20 FPS（极低负载 - 推荐）" },
            ["30 FPS (Power Saver / Low GPU & CPU)"] = new[] { "30 FPS (Oszczędny / Niskie zużycie)", "30 FPS (Energiesparmodus / Niedrige Last)", "30 FPS (Экономичный режим)", "30 FPS (Économie d'énergie)", "30 FPS (Risparmio risorse)", "30 FPS（省电 / 低占用）" },
            ["60 FPS (Balanced)"] = new[] { "60 FPS (Zbalansowany)", "60 FPS (Ausgewogen)", "60 FPS (Сбалансированный)", "60 FPS (Équilibré)", "60 FPS (Bilanciato)", "60 FPS（平衡）" },
            ["120 FPS (High Responsiveness)"] = new[] { "120 FPS (Wysoka responsywność)", "120 FPS (Hohe Reaktionszeit)", "120 FPS (Высокий отклик)", "120 FPS (Haute réactivité)", "120 FPS (Alta reattività)", "120 FPS（高响应）" },
            ["200 FPS (Uncapped)"] = new[] { "200 FPS (Bez limitu)", "200 FPS (Unbegrenzt)", "200 FPS (Без ограничения)", "200 FPS (Sans limite)", "200 FPS (Senza limiti)", "200 FPS（无限制）" },

            ["All Cores (Auto - {0} Cores)"] = new[] { "Wszystkie rdzenie (Auto - {0} rdzeni)", "Alle Kerne (Auto - {0} Kerne)", "Все ядра (Авто - {0} ядер)", "Tous les cœurs (Auto - {0} cœurs)", "Tutti i core (Auto - {0} core)", "全部核心（自动 - {0} 核心）" },
            ["2 Cores (Leave room for local client)"] = new[] { "2 rdzenie (Więcej mocy dla gry)", "2 Kerne (Platz für lokalen Client lassen)", "2 ядра (Оставить ресурсы для игры)", "2 cœurs (Laisser de la place au jeu)", "2 core (Lascia risorse al client)", "2 核心（为本机客户端留出性能）" },
            ["4 Cores (Recommended balanced)"] = new[] { "4 rdzenie (Zalecane zbalansowane)", "4 Kerne (Empfohlen ausgewogen)", "4 ядра (Рекомендуемый баланс)", "4 cœurs (Équilibre recommandé)", "4 core (Bilanciamento consigliato)", "4 核心（推荐平衡配置）" },
            ["6 Cores"] = new[] { "6 rdzeni", "6 Kerne", "6 ядер", "6 cœurs", "6 core", "6 核心" },
            ["8 Cores"] = new[] { "8 rdzeni", "8 Kerne", "8 ядер", "8 cœurs", "8 core", "8 核心" },

            ["Normal (Standard Windows Priority)"] = new[] { "Normalny (Standardowy priorytet Windows)", "Normal (Standard-Windows-Priorität)", "Обычный (Стандартный приоритет Windows)", "Normal (Priorité Windows standard)", "Normale (Priorità standard Windows)", "正常（Windows 标准优先级）" },
            ["Above Normal (Reduce Stutter)"] = new[] { "Powyżej normalnego (Mniejsze przycięcia)", "Höher als normal (Weniger Ruckler)", "Выше среднего (Меньше лагов)", "Supérieur à la normale (Moins de saccades)", "Superiore al normale (Riduce lag)", "高于正常（减少卡顿）" },
            ["High"] = new[] { "Wysoki", "Hoch", "Высокий", "Élevé", "Alto", "高" },
            ["Below Normal (Background Saver)"] = new[] { "Poniżej normalnego (Oszczędzanie w tle)", "Niedriger als normal (Hintergrundsparer)", "Ниже среднего (Экономия в фоне)", "Inférieur à la normale (Économie)", "Inferiore al normale (Risparmio)", "低于正常（后台省资源）" },

            // Game Rules Dropdown Options
            ["200 Troops (Max FPS / Low Spec)"] = new[] { "200 żołnierzy (Maks. FPS)", "200 Truppen (Max. FPS / Niedrig)", "200 воинов (Макс. FPS)", "200 soldats (FPS max)", "200 soldati (FPS massimi)", "200 人（最高帧率 / 低配）" },
            ["300 Troops"] = new[] { "300 żołnierzy", "300 Truppen", "300 воинов", "300 soldats", "300 soldati", "300 人" },
            ["400 Troops (Balanced - Recommended)"] = new[] { "400 żołnierzy (Zbalansowane - Zalecane)", "400 Truppen (Ausgewogen - Empfohlen)", "400 воинов (Сбалансированно - Рекомендуется)", "400 soldats (Équilibré - Recommandé)", "400 soldati (Bilanciato - Consigliato)", "400 人（平衡 - 推荐）" },
            ["500 Troops"] = new[] { "500 żołnierzy", "500 Truppen", "500 воинов", "500 soldats", "500 soldati", "500 人" },
            ["600 Troops (Large Battles)"] = new[] { "600 żołnierzy (Duże bitwy)", "600 Truppen (Große Schlachten)", "600 воинов (Крупные битвы)", "600 soldats (Grandes batailles)", "600 soldati (Grandi battaglie)", "600 人（大型会战）" },
            ["800 Troops (Epic Battles)"] = new[] { "800 żołnierzy (Epickie bitwy)", "800 Truppen (Epische Schlachten)", "800 воинов (Эпические сражения)", "800 soldats (Batailles épiques)", "800 soldati (Battaglie epiche)", "800 人（史诗战场）" },
            ["1000 Troops (Bannerlord Max)"] = new[] { "1000 żołnierzy (Maksimum gry)", "1000 Truppen (Bannerlord-Maximum)", "1000 воинов (Максимум игры)", "1000 soldats (Maximum du jeu)", "1000 soldati (Massimo del gioco)", "1000 人（游戏上限）" },

            ["Easy (33% Damage Taken)"] = new[] { "Łatwy (33% obrażeń)", "Einfach (33 % Schaden)", "Лёгкий (33% урона)", "Facile (33 % de dégâts)", "Facile (33% danni)", "简单（33% 承受伤害）" },
            ["Balanced (66% Damage - Recommended)"] = new[] { "Zbalansowany (66% obrażeń - Zalecane)", "Ausgewogen (66 % Schaden - Empfohlen)", "Сбалансированный (66% урона - Рекомендуется)", "Équilibré (66 % de dégâts - Recommandé)", "Bilanciato (66% danni - Consigliato)", "平衡（66% 承受伤害 - 推荐）" },
            ["Realistic (100% Full Damage)"] = new[] { "Realistyczny (100% pełne obrażenia)", "Realistisch (100 % voller Schaden)", "Реалистичный (100% полный урон)", "Réaliste (100 % dégâts réels)", "Realistico (100% danni reali)", "真实（100% 全额伤害）" },

            ["Poor / Easy AI"] = new[] { "Łatwa SI (Początkujący)", "Leichte KI (Anfänger)", "Слабый ИИ (Новичок)", "IA facile (Débutant)", "IA facile (Principiante)", "简单 AI" },
            ["Good / Balanced AI (Recommended)"] = new[] { "Normalna SI (Zbalansowana - Zalecane)", "Normale KI (Ausgewogen - Empfohlen)", "Хороший ИИ (Сбалансированный - Рекомендуется)", "IA normale (Équilibrée - Recommandé)", "IA normale (Bilanciata - Consigliato)", "正常 AI（平衡 - 推荐）" },
            ["Veteran / Challenging AI"] = new[] { "Weteran / Trudna SI", "Veteran / Anspruchsvolle KI", "Ветеран / Сложный ИИ", "IA vétéran / Difficile", "IA veterana / Difficile", "老兵 / 高难 AI" },

            ["Disabled (Heroes Knocked Unconscious Only)"] = new[] { "Wyłączona (Bohaterowie tylko tracą przytomność)", "Deaktiviert (Helden werden nur bewusstlos)", "Выключена (Герои только теряют сознание)", "Désactivée (Héros seulement assommés)", "Disattivata (Gli eroi svengono soltanto)", "关闭（英雄仅会昏迷）" },
            ["Companions Only Can Die"] = new[] { "Tylko towarzysze mogą zginąć", "Nur Gefährten können sterben", "Могут погибнуть только спутники", "Seuls les compagnons peuvent mourir", "Solo i compagni possono morire", "仅同伴可能阵亡" },
            ["Enabled for All Heroes (Realistic Death)"] = new[] { "Włączona dla wszystkich (Realistyczna śmierć)", "Für alle Helden aktiv (Realistischer Tod)", "Для всех героев (Реалистичная смерть)", "Activée pour tous les héros (Réaliste)", "Attiva per tutti gli eroi (Realistico)", "对所有英雄开启（真实死亡）" },

            ["5 Minutes"] = new[] { "5 minut", "5 Minuten", "5 минут", "5 minutes", "5 minuti", "5 分钟" },
            ["10 Minutes"] = new[] { "10 minut", "10 Minuten", "10 минут", "10 minutes", "10 minuti", "10 分钟" },
            ["15 Minutes (Recommended)"] = new[] { "15 minut (Zalecane)", "15 Minuten (Empfohlen)", "15 минут (Рекомендуется)", "15 minutes (Recommandé)", "15 minuti (Consigliato)", "15 分钟（推荐）" },
            ["30 Minutes"] = new[] { "30 minut", "30 Minuten", "30 минут", "30 minutes", "30 minuti", "30 分钟" },
            ["60 Minutes"] = new[] { "60 minut", "60 Minuten", "60 минут", "60 minutes", "60 minuti", "60 分钟" },
            ["Disabled"] = new[] { "Wyłączone", "Deaktiviert", "Отключено", "Désactivé", "Disattivato", "关闭" },

            ["0 Corpses (No Ragdolls - Max FPS)"] = new[] { "0 ciał (Maks. FPS)", "0 Leichen (Max. FPS)", "0 тел (Макс. FPS)", "0 cadavre (FPS max)", "0 cadaveri (FPS massimi)", "0 具尸体（最高帧率）" },
            ["5 Corpses"] = new[] { "5 ciał", "5 Leichen", "5 тел", "5 cadavres", "5 cadaveri", "5 具尸体" },
            ["20 Corpses (Balanced - Recommended)"] = new[] { "20 ciał (Zbalansowane - Zalecane)", "20 Leichen (Ausgewogen - Empfohlen)", "20 тел (Сбалансированно - Рекомендуется)", "20 cadavres (Équilibré - Recommandé)", "20 cadaveri (Bilanciato - Consigliato)", "20 具尸体（平衡 - 推荐）" },
            ["50 Corpses"] = new[] { "50 ciał", "50 Leichen", "50 тел", "50 cadavres", "50 cadaveri", "50 具尸体" },
            ["100 Corpses"] = new[] { "100 ciał", "100 Leichen", "100 тел", "100 cadavres", "100 cadaveri", "100 具尸体" },
            ["200 Corpses (Max)"] = new[] { "200 ciał (Maks.)", "200 Leichen (Max.)", "200 тел (Макс.)", "200 cadavres (Max)", "200 cadaveri (Massimo)", "200 具尸体（最大）" },

            ["2 Players (Duo)"] = new[] { "2 graczy (Duet)", "2 Spieler (Duo)", "2 игрока (Дуэт)", "2 joueurs (Duo)", "2 giocatori (Duo)", "2 名玩家（双人）" },
            ["4 Players (Small Group)"] = new[] { "4 graczy (Mała grupa)", "4 Spieler (Kleine Gruppe)", "4 игрока (Малая группа)", "4 joueurs (Petit groupe)", "4 giocatori (Piccolo gruppo)", "4 名玩家（小队）" },
            ["8 Players (Standard)"] = new[] { "8 graczy (Standard)", "8 Spieler (Standard)", "8 игроков (Стандарт)", "8 joueurs (Standard)", "8 giocatori (Standard)", "8 名玩家（标准）" },
            ["16 Players (Recommended)"] = new[] { "16 graczy (Zalecane)", "16 Spieler (Empfohlen)", "16 игроков (Рекомендуется)", "16 joueurs (Recommandé)", "16 giocatori (Consigliato)", "16 名玩家（推荐）" },
            ["24 Players"] = new[] { "24 graczy", "24 Spieler", "24 игрока", "24 joueurs", "24 giocatori", "24 名玩家" },
            ["32 Players (Large Campaign)"] = new[] { "32 graczy (Duża kampania)", "32 Spieler (Große Kampagne)", "32 игрока (Крупная кампания)", "32 joueurs (Grande campagne)", "32 giocatori (Grande campagna)", "32 名玩家（大型战役）" },
            ["64 Players (Max)"] = new[] { "64 graczy (Maks.)", "64 Spieler (Max.)", "64 игрока (Макс.)", "64 joueurs (Max)", "64 giocatori (Massimo)", "64 名玩家（上限）" },

            ["Public (Visible in Lobby)"] = new[] { "Publiczny (Widoczny w lobby)", "Öffentlich (In Lobby sichtbar)", "Публичный (Виден в лобби)", "Public (Visible dans le salon)", "Pubblico (Visibile nella lobby)", "公开（大厅可见）" },
            ["LAN Only (Local Network)"] = new[] { "Tylko LAN (Sieć lokalna)", "Nur LAN (Lokales Netzwerk)", "Только LAN (Локальная сеть)", "LAN uniquement (Réseau local)", "Solo LAN (Rete locale)", "仅局域网（LAN）" },
            ["Private (Direct IP Only)"] = new[] { "Prywatny (Tylko bezpośrednie IP)", "Privat (Nur Direkt-IP)", "Приватный (Только прямой IP)", "Privé (IP directe uniquement)", "Privato (Solo IP diretto)", "私密（仅限直连 IP）" },

            // Starting & Progression Dropdown Options
            ["Level 1 (Standard Start)"] = new[] { "Poziom 1 (Standardowy start)", "Stufe 1 (Standardstart)", "Уровень 1 (Обычный старт)", "Niveau 1 (Départ standard)", "Livello 1 (Inizio standard)", "1 级（标准开局）" },
            ["Level 5 (Seasoned Fighter)"] = new[] { "Poziom 5 (Zaprawiony wojownik)", "Stufe 5 (Erfahrener Kämpfer)", "Уровень 5 (Опытный боец)", "Niveau 5 (Combattant aguerri)", "Livello 5 (Combattente esperto)", "5 级（老练战士）" },
            ["Level 10 (Veteran Warrior)"] = new[] { "Poziom 10 (Weteran)", "Stufe 10 (Veteran)", "Уровень 10 (Воин-ветеран)", "Niveau 10 (Guerrier vétéran)", "Livello 10 (Guerriero veterano)", "10 级（精锐老兵）" },
            ["Level 15 (Commander)"] = new[] { "Poziom 15 (Dowódca)", "Stufe 15 (Kommandant)", "Уровень 15 (Командир)", "Niveau 15 (Commandant)", "Livello 15 (Comandante)", "15 级（指挥官）" },
            ["Level 20 (Master Lord)"] = new[] { "Poziom 20 (Mistrzowski lord)", "Stufe 20 (Meisterfürst)", "Уровень 20 (Мастер-лорд)", "Niveau 20 (Seigneur maître)", "Livello 20 (Signore maestro)", "20 级（大师领主）" },
            ["Level 25 (Legendary Leader)"] = new[] { "Poziom 25 (Legendarny przywódca)", "Stufe 25 (Legendärer Anführer)", "Уровень 25 (Легендарный лидер)", "Niveau 25 (Chef légendaire)", "Livello 25 (Leader leggendario)", "25 级（传奇统帅）" },
            ["Level 30 (Grand Sovereign)"] = new[] { "Poziom 30 (Wielki władca)", "Stufe 30 (Großherrscher)", "Уровень 30 (Великий правитель)", "Niveau 30 (Grand souverain)", "Livello 30 (Grande sovrano)", "30 级（至尊君主）" },

            ["0 Points"] = new[] { "0 punktów", "0 Punkte", "0 очков", "0 point", "0 punti", "0 点" },
            ["5 Points (Recommended)"] = new[] { "5 punktów (Zalecane)", "5 Punkte (Empfohlen)", "5 очков (Рекомендуется)", "5 points (Recommandé)", "5 punti (Consigliato)", "5 点（推荐）" },
            ["10 Points"] = new[] { "10 punktów", "10 Punkte", "10 очков", "10 points", "10 punti", "10 点" },
            ["15 Points"] = new[] { "15 punktów", "15 Punkte", "15 очков", "15 points", "15 punti", "15 点" },
            ["20 Points"] = new[] { "20 punktów", "20 Punkte", "20 очков", "20 points", "20 punti", "20 点" },
            ["30 Points"] = new[] { "30 punktów", "30 Punkte", "30 очков", "30 points", "30 punti", "30 点" },
            ["50 Points"] = new[] { "50 punktów", "50 Punkte", "50 очков", "50 points", "50 punti", "50 点" },
            ["2 Points (Standard)"] = new[] { "2 punkty (Standard)", "2 Punkte (Standard)", "2 очка (Стандарт)", "2 points (Standard)", "2 punti (Standard)", "2 点（标准）" },
            ["4 Points"] = new[] { "4 punkty", "4 Punkte", "4 очка", "4 points", "4 punti", "4 点" },
            ["6 Points"] = new[] { "6 punktów", "6 Punkte", "6 очков", "6 points", "6 punti", "6 点" },
            ["8 Points (Strong Start)"] = new[] { "8 punktów (Mocny start)", "8 Punkte (Starker Start)", "8 очков (Сильный старт)", "8 points (Départ fort)", "8 punti (Inizio forte)", "8 点（强力开局）" },
            ["12 Points"] = new[] { "12 punktów", "12 Punkte", "12 очков", "12 points", "12 punti", "12 点" },
            ["20 Points (Max)"] = new[] { "20 punktów (Maks.)", "20 Punkte (Max.)", "20 очков (Макс.)", "20 points (Max)", "20 punti (Massimo)", "20 点（最高）" },

            ["0 (Vanilla Character Background)"] = new[] { "0 (Domyślne z historii postaci)", "0 (Standard-Charakterhintergrund)", "0 (По истории персонажа)", "0 (Selon l'origine du personnage)", "0 (Origine standard del personaggio)", "0（按角色背景默认）" },
            ["25 (All Skills Min 25)"] = new[] { "25 (Wszystkie umiejętności min. 25)", "25 (Alle Fertigkeiten min. 25)", "25 (Все навыки мин. 25)", "25 (Toutes compétences min. 25)", "25 (Tutte le abilità min. 25)", "25（所有技能最低 25）" },
            ["50 (All Skills Min 50)"] = new[] { "50 (Wszystkie umiejętności min. 50)", "50 (Alle Fertigkeiten min. 50)", "50 (Все навыки мин. 50)", "50 (Toutes compétences min. 50)", "50 (Tutte le abilità min. 50)", "50（所有技能最低 50）" },
            ["100 (All Skills Min 100)"] = new[] { "100 (Wszystkie umiejętności min. 100)", "100 (Alle Fertigkeiten min. 100)", "100 (Все навыки мин. 100)", "100 (Toutes compétences min. 100)", "100 (Tutte le abilità min. 100)", "100（所有技能最低 100）" },
            ["150 (All Skills Min 150)"] = new[] { "150 (Wszystkie umiejętności min. 150)", "150 (Alle Fertigkeiten min. 150)", "150 (Все навыки мин. 150)", "150 (Toutes compétences min. 150)", "150 (Tutte le abilità min. 150)", "150（所有技能最低 150）" },

            ["1.0x (Standard Speed)"] = new[] { "1.0x (Standardowa prędkość)", "1,0x (Standardgeschwindigkeit)", "1.0x (Стандартная скорость)", "1.0x (Vitesse standard)", "1.0x (Velocità standard)", "1.0x（标准速度）" },
            ["1.5x (+50% Faster)"] = new[] { "1.5x (+50% szybciej)", "1,5x (+50 % schneller)", "1.5x (+50% быстрее)", "1.5x (+50 % plus rapide)", "1.5x (+50% più veloce)", "1.5x（快 50%）" },
            ["2.0x (2x Level Speed)"] = new[] { "2.0x (2x szybsze poziomy)", "2,0x (2x schnelleres Leveln)", "2.0x (В 2 раза быстрее)", "2.0x (Progression x2)", "2.0x (Velocità 2x)", "2.0x（双倍升级速度）" },
            ["3.0x (3x Campaign)"] = new[] { "3.0x (3x szybsza kampania)", "3,0x (3x Kampagne)", "3.0x (В 3 раза быстрее)", "3.0x (Campagne x3)", "3.0x (Campagna 3x)", "3.0x（三倍经验）" },
            ["5.0x (5x Fast)"] = new[] { "5.0x (5x szybciej)", "5,0x (5x schnell)", "5.0x (В 5 раз быстрее)", "5.0x (Rapide x5)", "5.0x (Veloce 5x)", "5.0x（五倍速）" },
            ["10.0x (10x Rapid)"] = new[] { "10.0x (10x błyskawicznie)", "10,0x (10x rasant)", "10.0x (В 10 раз быстрее)", "10.0x (Très rapide x10)", "10.0x (Rapido 10x)", "10.0x（十倍极速）" },

            ["1.0x (Standard Skill Gain)"] = new[] { "1.0x (Standardowy rozwój umiejętności)", "1,0x (Standard-Fertigkeitsgewinn)", "1.0x (Стандартный рост навыков)", "1.0x (Gain standard)", "1.0x (Crescita standard)", "1.0x（标准技能成长）" },
            ["1.5x (+50% Skill Gain)"] = new[] { "1.5x (+50% rozwoju umiejętności)", "1,5x (+50 % Fertigkeitsgewinn)", "1.5x (+50% роста навыков)", "1.5x (+50 % de gain)", "1.5x (+50% abilità)", "1.5x（+50% 技能经验）" },
            ["2.0x (2x Skill Gain)"] = new[] { "2.0x (2x szybsze umiejętności)", "2,0x (2x Fertigkeitsgewinn)", "2.0x (2x рост навыков)", "2.0x (Gain x2)", "2.0x (Abilità 2x)", "2.0x（双倍技能经验）" },
            ["3.0x (3x Mastery)"] = new[] { "3.0x (3x mistrzostwo)", "3,0x (3x Meisterschaft)", "3.0x (3x мастерство)", "3.0x (Maîtrise x3)", "3.0x (Maestria 3x)", "3.0x（三倍精通）" },
            ["5.0x (5x Skill Gain)"] = new[] { "5.0x (5x szybsze umiejętności)", "5,0x (5x Fertigkeitsgewinn)", "5.0x (5x рост навыков)", "5.0x (Gain x5)", "5.0x (Abilità 5x)", "5.0x（五倍技能经验）" },
            ["10.0x (10x Instant Mastery)"] = new[] { "10.0x (10x błyskawiczne mistrzostwo)", "10,0x (10x Sofort-Meisterschaft)", "10.0x (10x мгновенное мастерство)", "10.0x (Maîtrise rapide x10)", "10.0x (Maestria istantanea 10x)", "10.0x（十倍瞬间精通）" },

            ["1,000 Denars (Standard)"] = new[] { "1 000 denarów (Standard)", "1.000 Denare (Standard)", "1 000 динаров (Стандарт)", "1 000 deniers (Standard)", "1.000 denari (Standard)", "1,000 第纳尔（标准）" },
            ["5,000 Denars (Modest)"] = new[] { "5 000 denarów (Skromny)", "5.000 Denare (Bescheiden)", "5 000 динаров (Скромный)", "5 000 deniers (Modeste)", "5.000 denari (Modesto)", "5,000 第纳尔（小康）" },
            ["10,000 Denars (Balanced)"] = new[] { "10 000 denarów (Zbalansowany)", "10.000 Denare (Ausgewogen)", "10 000 динаров (Сбалансированный)", "10 000 deniers (Équilibré)", "10.000 denari (Bilanciato)", "10,000 第纳尔（平衡）" },
            ["25,000 Denars (Comfortable)"] = new[] { "25 000 denarów (Wygodny)", "25.000 Denare (Komfortabel)", "25 000 динаров (Комфортный)", "25 000 deniers (Confortable)", "25.000 denari (Comodo)", "25,000 第纳尔（充裕）" },
            ["50,000 Denars (Wealthy Lord)"] = new[] { "50 000 denarów (Bogaty lord)", "50.000 Denare (Wohlhabender Fürst)", "50 000 динаров (Богатый лорд)", "50 000 deniers (Seigneur riche)", "50.000 denari (Lord benestante)", "50,000 第纳尔（富豪领主）" },
            ["100,000 Denars (Treasury)"] = new[] { "100 000 denarów (Skarbiec)", "100.000 Denare (Schatzkammer)", "100 000 динаров (Казна)", "100 000 deniers (Trésor)", "100.000 denari (Tesoro)", "100,000 第纳尔（国库）" },
            ["500,000 Denars (Kingdom Founder)"] = new[] { "500 000 denarów (Założyciel królestwa)", "500.000 Denare (Königreichgründer)", "500 000 динаров (Основатель королевства)", "500 000 deniers (Fondateur de royaume)", "500.000 denari (Fondatore di regno)", "500,000 第纳尔（建国资金）" },

            ["0 Renown (Clan Tier 0)"] = new[] { "0 sławy (Poziom klanu 0)", "0 Ansehen (Clan-Stufe 0)", "0 известности (Уровень клана 0)", "0 renommée (Rang de clan 0)", "0 fama (Grado clan 0)", "0 声望（家族 0 级）" },
            ["50 Renown (Clan Tier 1 - Mercenary)"] = new[] { "50 sławy (Klan 1 - Najemnik)", "50 Ansehen (Clan-Stufe 1 - Söldner)", "50 известности (Клан 1 - Наёмник)", "50 renommée (Clan 1 - Mercenaire)", "50 fama (Clan 1 - Mercenario)", "50 声望（家族 1 级 - 雇佣兵）" },
            ["150 Renown (Clan Tier 2 - Vassal)"] = new[] { "150 sławy (Klan 2 - Wasal)", "150 Ansehen (Clan-Stufe 2 - Vasall)", "150 известности (Клан 2 - Вассал)", "150 renommée (Clan 2 - Vassal)", "150 fama (Clan 2 - Vassallo)", "150 声望（家族 2 级 - 封臣）" },
            ["350 Renown (Clan Tier 3 - Kingdom Ready)"] = new[] { "350 sławy (Klan 3 - Gotowy na królestwo)", "350 Ansehen (Clan-Stufe 3 - Königreichbereit)", "350 известности (Клан 3 - Готов к королевству)", "350 renommée (Clan 3 - Prêt au royaume)", "350 fama (Clan 3 - Pronto per un regno)", "350 声望（家族 3 级 - 可自立建国）" },
            ["900 Renown (Clan Tier 4 - Great House)"] = new[] { "900 sławy (Klan 4 - Wielki Ród)", "900 Ansehen (Clan-Stufe 4 - Großes Haus)", "900 известности (Клан 4 - Великий дом)", "900 renommée (Clan 4 - Grande maison)", "900 fama (Clan 4 - Grande Casata)", "900 声望（家族 4 级 - 显赫世家）" },

            ["0 Influence"] = new[] { "0 wpływów", "0 Einfluss", "0 влияния", "0 influence", "0 influenza", "0 影响力" },
            ["50 Influence (Council Vote)"] = new[] { "50 wpływów (Głos w radzie)", "50 Einfluss (Ratsstimme)", "50 влияния (Голос в совете)", "50 influence (Vote au conseil)", "50 influenza (Voto in consiglio)", "50 影响力（议会投票）" },
            ["100 Influence (Policy & Fiefs)"] = new[] { "100 wpływów (Ustawy i lenna)", "100 Einfluss (Gesetze & Lehen)", "100 влияния (Законы и наделы)", "100 influence (Lois et fiefs)", "100 influenza (Leggi e feudi)", "100 影响力（法案与封地）" },
            ["250 Influence (Raise Army)"] = new[] { "250 wpływów (Zwołanie armii)", "250 Einfluss (Armee ausheben)", "250 влияния (Сбор армии)", "250 influence (Lever une armée)", "250 influenza (Raduna armata)", "250 影响力（组建军团）" },
            ["500 Influence (High Authority)"] = new[] { "500 wpływów (Wysoki autorytet)", "500 Einfluss (Hohe Autorität)", "500 влияния (Высокий авторитет)", "500 influence (Haute autorité)", "500 influenza (Alta autorità)", "500 影响力（至高威望）" },

            ["Standard Vanilla Equipment"] = new[] { "Standardowy ekwipunek gry", "Standard-Spielausrüstung", "Стандартное снаряжение", "Équipement de base du jeu", "Equipaggiamento base del gioco", "原版默认装备" },
            ["Recruit Kit (Tier 2-3 Sword, Shield, Bow, Armor, Horse)"] = new[] { "Zestaw rekruta (Miecz T2-3, tarcza, łuk, zbroja, koń)", "Rekruten-Set (T2-3 Schwert, Schild, Bogen, Rüstung, Pferd)", "Набор бойца (Меч T2-3, щит, лук, броня, конь)", "Kit recrue (Épée T2-3, bouclier, arc, armure, cheval)", "Kit recluta (Spada T2-3, scudo, arco, armatura, cavallo)", "新兵套装（2-3 级剑、盾、弓、护甲、坐骑）" },
            ["Knight / Noble Kit (Tier 4-5 Armor, Warhorse, Lance)"] = new[] { "Zestaw rycerza (Zbroja T4-5, koń bojowy, lanca)", "Ritter-/Adels-Set (T4-5 Rüstung, Schlachtross, Lanze)", "Набор рыцаря (Броня T4-5, боевой конь, копьё)", "Kit chevalier (Armure T4-5, destrier, lance)", "Kit cavaliere (Armatura T4-5, destriero, lancia)", "骑士 / 贵族套装（4-5 级重甲、战马、骑枪）" },
            ["Royal Elite Kit (Tier 6 Full Plate, Noble Bow & Destrier)"] = new[] { "Zestaw królewski (Pełna zbroja T6, szlachetny łuk, rumak)", "Königliches Elite-Set (T6 Vollplatte, Edelbogen & Schlachtross)", "Королевский набор (Латы T6, благородный лук, скакун)", "Kit royal d'élite (Armure T6, arc noble et destrier)", "Kit reale d'élite (Armatura T6, arco nobile e destriero)", "皇家精锐套装（6 级板甲、贵族弓、顶级战马）" },
            ["Merchant Caravan Kit (16 Mules & Trade Goods)"] = new[] { "Zestaw kupca (16 mułów, towary handlowe i 50 zboża)", "Händler-Karawanen-Set (16 Maultiere & Handelswaren)", "Набор купца (16 мулов, товары и 50 зерна)", "Kit marchand (16 mules et marchandises)", "Kit mercante (16 muli e merci di scambio)", "商队套装（16 匹骡马、贸易商品及 50 粮食）" },

            ["Solo Start (0 Troops)"] = new[] { "Start w pojedynkę (0 żołnierzy)", "Alleine starten (0 Truppen)", "В одиночку (0 воинов)", "Départ en solo (0 soldat)", "Inizio da solo (0 soldati)", "单人开局（0 名士兵）" },
            ["Small Retinue (10 Culture Troops)"] = new[] { "Mała świta (10 żołnierzy kultury)", "Kleines Gefolge (10 Kulturtruppen)", "Малый отряд (10 воинов культуры)", "Petite escorte (10 soldats)", "Piccolo seguito (10 soldati)", "小型护卫（10 名本文化士兵）" },
            ["Balanced Warband (20 Troops + 5 Noble Cavalry)"] = new[] { "Średni oddział (20 żołnierzy + 5 szlachetnych)", "Ausgewogene Truppe (20 Soldaten + 5 Edelreiter)", "Средний отряд (20 воинов + 5 знатных)", "Troupe équilibrée (20 soldats + 5 nobles)", "Gruppo bilanciato (20 soldati + 5 nobili)", "均衡部队（20 名士兵 + 5 名贵族骑兵）" },
            ["Army Core (35 Troops + 15 Elite Guard)"] = new[] { "Trzon armii (35 żołnierzy + 15 elitarnych)", "Armeekern (35 Soldaten + 15 Elitegarde)", "Костяк армии (35 воинов + 15 элиты)", "Noyau d'armée (35 soldats + 15 gardes d'élite)", "Nucleo d'armata (35 soldati + 15 guardie d'élite)", "精锐主力（35 名士兵 + 15 名精锐卫队）" },

            // Startup Auto-Update Notifications
            ["New version available: {0} (Current: {1})"] = new[] { "Dostępna nowa wersja: {0} (Obecna: {1})", "Neue Version verfügbar: {0} (Aktuell: {1})", "Доступна новая версия: {0} (Текущая: {1})", "Nouvelle version disponible : {0} (Actuelle : {1})", "Nuova versione disponibile: {0} (Attuale: {1})", "发现新版本：{0}（当前：{1}）" },
            ["New Update Available"] = new[] { "Dostępna aktualizacja", "Update verfügbar", "Доступно обновление", "Mise à jour disponible", "Aggiornamento disponibile", "发现新版本" },
            ["A new version of Calradia Connected is available!\n\nCurrent Version: {0}\nLatest Version: {1}\n\nWould you like to download and install the update now?"] = new[] { "Dostępna jest nowa wersja Calradia Connected!\n\nObecna wersja: {0}\nNajnowsza wersja: {1}\n\nCzy chcesz pobrać i zainstalować aktualizację teraz?", "Eine neue Version von Calradia Connected ist verfügbar!\n\nAktuelle Version: {0}\nNeueste Version: {1}\n\nMöchtest du das Update jetzt herunterladen und installieren?", "Доступна новая версия Calradia Connected!\n\nТекущая версия: {0}\nНовая версия: {1}\n\nЗагрузить и установить обновление сейчас?", "Une nouvelle version de Calradia Connected est disponible !\n\nVersion actuelle : {0}\nDernière version : {1}\n\nVoulez-vous télécharger et installer la mise à jour maintenant ?", "È disponibile una nuova versione di Calradia Connected!\n\nVersione attuale: {0}\nUltima versione: {1}\n\nVuoi scaricare e installare l'aggiornamento ora?", "Calradia Connected 有新版本可用！\n\n当前版本：{0}\n最新版本：{1}\n\n是否立即下载并安装更新？" }
        };
    }
}
