﻿using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Lampac.Engine.CORE;
using Lampac.Engine.Parse;
using Lampac.Engine;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using Lampac.Models.JAC;
using System.Collections.Generic;

namespace Lampac.Controllers.JAC
{
    [Route("nnmclub/[action]")]
    public class NNMClubController : BaseController
    {
        #region Cookie / TakeLogin
        static string Cookie;

        async static void TakeLogin()
        {
            string authKey = "nnmclub:TakeLogin()";
            if (Startup.memoryCache.TryGetValue(authKey, out _))
                return;

            Startup.memoryCache.Set(authKey, 0, TimeSpan.FromMinutes(2));

            try
            {
                var clientHandler = new System.Net.Http.HttpClientHandler()
                {
                    AllowAutoRedirect = false
                };

                clientHandler.ServerCertificateCustomValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
                using (var client = new System.Net.Http.HttpClient(clientHandler))
                {
                    client.Timeout = TimeSpan.FromSeconds(AppInit.conf.jac.timeoutSeconds);
                    client.MaxResponseContentBufferSize = 2000000; // 2MB
                    client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/99.0.4844.51 Safari/537.36");
                    client.DefaultRequestHeaders.Add("cache-control", "no-cache");
                    client.DefaultRequestHeaders.Add("dnt", "1");
                    client.DefaultRequestHeaders.Add("origin", AppInit.conf.NNMClub.host);
                    client.DefaultRequestHeaders.Add("pragma", "no-cache");
                    client.DefaultRequestHeaders.Add("referer", $"{AppInit.conf.NNMClub.host}/");
                    client.DefaultRequestHeaders.Add("upgrade-insecure-requests", "1");

                    var postParams = new Dictionary<string, string>();
                    postParams.Add("redirect", "%2F");
                    postParams.Add("username", AppInit.conf.NNMClub.login.u);
                    postParams.Add("password", AppInit.conf.NNMClub.login.p);
                    postParams.Add("autologin", "on");
                    postParams.Add("login", "%C2%F5%EE%E4");

                    using (var postContent = new System.Net.Http.FormUrlEncodedContent(postParams))
                    {
                        using (var response = await client.PostAsync($"{AppInit.conf.NNMClub.host}/forum/login.php", postContent))
                        {
                            if (response.Headers.TryGetValues("Set-Cookie", out var cook))
                            {
                                string data = null, sid = null;
                                foreach (string line in cook)
                                {
                                    if (string.IsNullOrWhiteSpace(line))
                                        continue;

                                    if (line.Contains("phpbb2mysql_4_data="))
                                        data = new Regex("phpbb2mysql_4_data=([^;]+)(;|$)").Match(line).Groups[1].Value;

                                    if (line.Contains("phpbb2mysql_4_sid="))
                                        sid = new Regex("phpbb2mysql_4_sid=([^;]+)(;|$)").Match(line).Groups[1].Value;
                                }

                                if (!string.IsNullOrWhiteSpace(data) && !string.IsNullOrWhiteSpace(sid))
                                    Cookie = $"phpbb2mysql_4_data={data}; phpbb2mysql_4_sid={sid};";
                            }
                        }
                    }
                }
            }
            catch { }
        }
        #endregion

        #region parseMagnet
        static string TorrentFileMemKey(string id) => $"nnmclub:parseMagnet:download:{id}";

        static string TorrentMagnetMemKey(string id) => $"nnmclub:parseMagnet:{id}";


        async public Task<ActionResult> parseMagnet(string id, bool usecache)
        {
            if (!AppInit.conf.NNMClub.enable)
                return Content("disable");

            #region Кеш torrent
            string keydownload = TorrentFileMemKey(id);
            if (Startup.memoryCache.TryGetValue(keydownload, out byte[] _f))
                return File(_f, "application/x-bittorrent");

            string keymagnet = TorrentMagnetMemKey(id);
            if (Startup.memoryCache.TryGetValue(keymagnet, out string _m))
                return Redirect(_m);
            #endregion

            #region usecache / emptycache
            string keyerror = $"nnmclub:parseMagnet:{id}:error";
            if (usecache || Startup.memoryCache.TryGetValue(keyerror, out _))
            {
                if (await TorrentCache.Read(keydownload) is var tcache && tcache.cache)
                    return File(tcache.torrent, "application/x-bittorrent");

                if (await TorrentCache.ReadMagnet(keymagnet) is var mcache && mcache.cache)
                    Redirect(mcache.torrent);

                return Content("error");
            }
            #endregion

            #region html
            string html = await HttpClient.Get($"{AppInit.conf.NNMClub.host}/forum/viewtopic.php?t=" + id, useproxy: AppInit.conf.NNMClub.useproxy, timeoutSeconds: 10);
            string magnet = new Regex("href=\"(magnet:[^\"]+)\" title=\"Примагнититься\"").Match(html ?? string.Empty).Groups[1].Value;

            if (html == null || !html.Contains("NNM-Club</title>") || string.IsNullOrWhiteSpace(magnet))
            {
                if (AppInit.conf.jac.emptycache)
                    Startup.memoryCache.Set(keyerror, 0, DateTime.Now.AddMinutes(AppInit.conf.jac.torrentCacheToMinutes));

                if (await TorrentCache.Read(keydownload) is var tcache && tcache.cache)
                    return File(tcache.torrent, "application/x-bittorrent");

                if (await TorrentCache.ReadMagnet(keymagnet) is var mcache && mcache.cache)
                    Redirect(mcache.torrent);

                return Content("error");
            }
            #endregion

            #region download torrent
            if (Cookie != null)
            {
                string downloadid = new Regex("href=\"download\\.php\\?id=([0-9]+)\"").Match(html).Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(downloadid))
                {
                    byte[] _t = await HttpClient.Download($"{AppInit.conf.NNMClub.host}/forum/download.php?id={downloadid}", cookie: Cookie, referer: AppInit.conf.NNMClub.host, timeoutSeconds: 10);
                    if (_t != null && BencodeTo.Magnet(_t) != null)
                    {
                        await TorrentCache.Write(keydownload, _t);
                        Startup.memoryCache.Set(keydownload, _t, DateTime.Now.AddMinutes(AppInit.conf.jac.torrentCacheToMinutes));
                        return File(_t, "application/x-bittorrent");
                    }
                }
            }
            #endregion

            await TorrentCache.Write(keymagnet, magnet);
            Startup.memoryCache.Set(keymagnet, magnet, DateTime.Now.AddMinutes(AppInit.conf.jac.torrentCacheToMinutes));
            return Redirect(magnet);
        }
        #endregion

        #region parsePage
        async public static Task<bool> parsePage(string host, ConcurrentBag<TorrentDetails> torrents, string query, string[] cats)
        {
            if (!AppInit.conf.NNMClub.enable)
                return false;

            #region Кеш html
            string cachekey = $"nnmclub:{string.Join(":", cats ?? new string[] { })}:{query}";
            var cread = await HtmlCache.Read(cachekey);
            bool validrq = cread.cache;

            if (cread.emptycache)
                return false;

            if (!cread.cache)
            {
                string data = $"prev_sd=0&prev_a=0&prev_my=0&prev_n=0&prev_shc=0&prev_shf=1&prev_sha=1&prev_shs=0&prev_shr=0&prev_sht=0&o=1&s=2&tm=-1&shf=1&sha=1&ta=-1&sns=-1&sds=-1&nm={HttpUtility.UrlEncode(query, Encoding.GetEncoding(1251))}&pn=&submit=%CF%EE%E8%F1%EA";
                string html = await HttpClient.Post($"{AppInit.conf.NNMClub.host}/forum/tracker.php", new System.Net.Http.StringContent(data, Encoding.UTF8, "application/x-www-form-urlencoded"), encoding: Encoding.GetEncoding(1251), useproxy: AppInit.conf.NNMClub.useproxy, timeoutSeconds: AppInit.conf.jac.timeoutSeconds);

                if (html != null && html.Contains("NNM-Club</title>"))
                {
                    cread.html = html;
                    await HtmlCache.Write(cachekey, html);
                    validrq = true;

                    if (!html.Contains(">Выход") && !string.IsNullOrWhiteSpace(AppInit.conf.NNMClub.login.u) && !string.IsNullOrWhiteSpace(AppInit.conf.NNMClub.login.p))
                        TakeLogin();
                }

                if (cread.html == null)
                {
                    HtmlCache.EmptyCache(cachekey);
                    return false;
                }
            }
            #endregion

            foreach (string row in cread.html.Split("</tr>"))
            {
                #region Локальный метод - Match
                string Match(string pattern, int index = 1)
                {
                    string res = HttpUtility.HtmlDecode(new Regex(pattern, RegexOptions.IgnoreCase).Match(row).Groups[index].Value.Trim());
                    res = Regex.Replace(res, "[\n\r\t ]+", " ");
                    return res.Trim();
                }
                #endregion

                #region createTime
                DateTime createTime = tParse.ParseCreateTime(Match("title=\"Добавлено\" class=\"gensmall\"><u>[0-9]+</u> ([0-9]{2}-[0-9]{2}-[0-9]{4}<br>[^<]+)</td>").Replace("<br>", " "), "dd-MM-yyyy HH:mm");
                //if (createTime == default)
                //    continue;
                #endregion

                #region Данные раздачи
                string url = Match("href=\"(viewtopic.php\\?t=[0-9]+)\"");
                string viewtopic = Match("href=\"viewtopic.php\\?t=([0-9]+)\"");
                string tracker = Match("class=\"gen\" href=\"tracker.php\\?f=([0-9]+)");

                string title = Match("class=\"genmed topictitle\" [^>]+><b>([^<]+)</b>");
                string _sid = Match("class=\"seedmed\"><b>([0-9]+)</b><");
                string _pir = Match("class=\"leechmed\"><b>([0-9]+)</b></td>");
                string sizeName = Match("class=\"gensmall\"><u>[^<]+</u> ([^<]+)</td>");

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(viewtopic) || string.IsNullOrWhiteSpace(tracker))
                    continue;

                if (tracker == "913" && !title.Contains("UKR"))
                    continue;

                url = $"{AppInit.conf.NNMClub.host}/{url}";
                #endregion

                #region Парсим раздачи
                int relased = 0;
                string name = null, originalname = null;

                if (tracker is "225" or "227" or "913" or "218" or "954" or "1293" or "1296" or "1299" or "682" or "884" or "693"
                    or "768" or "713" or "576" or "610")
                {
                    #region Новинки кино / Зарубежное кино / Зарубежные сериалы / Док. TV-бренды / Док. и телепередачи
                    // Крестная мама (Наркомама) / La Daronne / Mama Weed (2020)
                    var g = Regex.Match(title, "^([^/\\(\\|]+) \\([^\\)]+\\) / [^/\\(\\|]+ / ([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                    {
                        name = g[1].Value;
                        originalname = g[2].Value;

                        if (int.TryParse(g[3].Value, out int _yer))
                            relased = _yer;
                    }
                    else
                    {
                        // Связанный груз / Белые рабыни-девственницы / Bound Cargo / White Slave Virgins (2003) DVDRip
                        g = Regex.Match(title, "^([^/\\(\\|]+) / [^/\\(\\|]+ / [^/\\(\\|]+ / ([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                        if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                        {
                            name = g[1].Value;
                            originalname = g[2].Value;

                            if (int.TryParse(g[3].Value, out int _yer))
                                relased = _yer;
                        }
                        else
                        {
                            // Академия монстров / Escuela de Miedo / Cranston Academy: Monster Zone (2020)
                            g = Regex.Match(title, "^([^/\\(\\|]+) / [^/\\(\\|]+ / ([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                            if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                            {
                                name = g[1].Value;
                                originalname = g[2].Value;

                                if (int.TryParse(g[3].Value, out int _yer))
                                    relased = _yer;
                            }
                            else
                            {
                                // Воображаемая реальность (Долина богов) / Valley of the Gods (2019)
                                g = Regex.Match(title, "^([^/\\(\\|]+) \\([^\\)]+\\) / ([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                                if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                                {
                                    name = g[1].Value;
                                    originalname = g[2].Value;

                                    if (int.TryParse(g[3].Value, out int _yer))
                                        relased = _yer;
                                }
                                else
                                {
                                    // Страна грёз / Dreamland (2019)
                                    g = Regex.Match(title, "^([^/\\(\\|]+) / ([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                                    {
                                        name = g[1].Value;
                                        originalname = g[2].Value;

                                        if (int.TryParse(g[3].Value, out int _yer))
                                            relased = _yer;
                                    }
                                    else
                                    {
                                        // Тайны анатомии (Мозг) (2020)
                                        g = Regex.Match(title, "^([^/\\(\\|]+) \\([^\\)]+\\) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                                        if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
                                        {
                                            name = g[1].Value;
                                            if (int.TryParse(g[2].Value, out int _yer))
                                                relased = _yer;
                                        }
                                        else
                                        {
                                            // Презумпция виновности (2020)
                                            g = Regex.Match(title, "^([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;

                                            name = g[1].Value;
                                            if (int.TryParse(g[2].Value, out int _yer))
                                                relased = _yer;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    #endregion
                }
                else if (tracker is "270" or "221" or "882")
                {
                    #region Наше кино
                    var g = Regex.Match(title, "^([^/\\(\\|]+) \\(([0-9]{4})\\)").Groups;
                    name = g[1].Value;

                    if (int.TryParse(g[2].Value, out int _yer))
                        relased = _yer;
                    #endregion
                }
                else if (tracker == "769")
                {
                    #region Наши сериалы
                    // Теория вероятности / Игрок (2020)
                    var g = Regex.Match(title, "^([^/\\(\\|]+) / [^/\\(\\|]+ \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
                    {
                        name = g[1].Value;
                        if (int.TryParse(g[2].Value, out int _yer))
                            relased = _yer;
                    }
                    else
                    {
                        // Тайны следствия (2020)
                        g = Regex.Match(title, "^([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                        name = g[1].Value;

                        if (int.TryParse(g[2].Value, out int _yer))
                            relased = _yer;
                    }
                    #endregion
                }
                else if (tracker is "623" or "622" or "621" or "632" or "627" or "626" or "625" or "644")
                {
                    #region Аниме и Манга
                    // Black Clover (2017) | Чёрный клевер (часть 2) [2017(-2021)?,
                    var g = Regex.Match(title, "^([^/\\[\\(]+) \\([0-9]{4}\\) \\| ([^/\\[\\(]+) \\([^\\)]+\\) \\[([0-9]{4})(-[0-9]{4})?,").Groups;
                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                    {
                        name = g[2].Value;
                        originalname = g[1].Value;

                        if (int.TryParse(g[3].Value, out int _yer))
                            relased = _yer;
                    }
                    else
                    {
                        // Black Clover (2017) | Чёрный клевер [2017(-2021)?,
                        g = Regex.Match(title, "^([^/\\[\\(]+) \\([0-9]{4}\\) \\| ([^/\\[\\(]+) \\[([0-9]{4})(-[0-9]{4})?,").Groups;
                        if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[3].Value))
                        {
                            name = g[2].Value;
                            originalname = g[1].Value;

                            if (int.TryParse(g[3].Value, out int _yer))
                                relased = _yer;
                        }
                        else
                        {
                            // Tunshi Xingkong | Swallowed Star | Пожиратель звёзд | Поглощая звезду [2020(-2021)?,
                            // Tunshi Xingkong | Swallowed Star | Пожиратель звёзд | Поглощая звезду [ТВ-1] [2020(-2021)?,
                            g = Regex.Match(title, "^([^/\\[\\(]+) \\| [^/\\[\\(]+ \\| [^/\\[\\(]+ \\| ([^/\\[\\(]+) (\\[(ТВ|TV)-[0-9]+\\] )?\\[([0-9]{4})(-[0-9]{4})?,").Groups;
                            if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[5].Value))
                            {
                                name = g[2].Value;
                                originalname = g[1].Value;

                                if (int.TryParse(g[5].Value, out int _yer))
                                    relased = _yer;
                            }
                            else
                            {
                                // Uzaki-chan wa Asobitai! | Uzaki-chan Wants to Hang Out! | Узаки хочет тусоваться! (Удзаки хочет погулять!) [2020(-2021)?,
                                // Uzaki-chan wa Asobitai! | Uzaki-chan Wants to Hang Out! | Узаки хочет тусоваться! (Удзаки хочет погулять!) [ТВ-1] [2020(-2021)?,
                                g = Regex.Match(title, "^([^/\\[\\(]+) \\| [^/\\[\\(]+ \\| ([^/\\[\\(]+) \\([^\\)]+\\) (\\[(ТВ|TV)-[0-9]+\\] )?\\[([0-9]{4})(-[0-9]{4})?,").Groups;
                                if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[5].Value))
                                {
                                    name = g[2].Value;
                                    originalname = g[1].Value;

                                    if (int.TryParse(g[5].Value, out int _yer))
                                        relased = _yer;
                                }
                                else
                                {
                                    // Kanojo, Okarishimasu | Rent-A-Girlfriend | Девушка на час [ТВ-1] [2020(-2021)?,
                                    // Kusoge-tte Iuna! | Don`t Call Us a Junk Game! | Это вам не трешовая игра! [2020(-2021)?,
                                    g = Regex.Match(title, "^([^/\\[\\(]+) \\| [^/\\[\\(]+ \\| ([^/\\[\\(]+) (\\[(ТВ|TV)-[0-9]+\\] )?\\[([0-9]{4})(-[0-9]{4})?,").Groups;
                                    if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[5].Value))
                                    {
                                        name = g[2].Value;
                                        originalname = g[1].Value;

                                        if (int.TryParse(g[5].Value, out int _yer))
                                            relased = _yer;
                                    }
                                    else
                                    {
                                        // Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season | Re: Жизнь в альтернативном мире с нуля [ТВ-2] [2020(-2021)?,
                                        // Hortensia Saga | Сага о гортензии [2021(-2021)?,
                                        g = Regex.Match(title, "^([^/\\[\\(]+) \\| ([^/\\[\\(]+) (\\[(ТВ|TV)-[0-9]+\\] )?\\[([0-9]{4})(-[0-9]{4})?,").Groups;
                                        if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[5].Value))
                                        {
                                            name = g[2].Value;
                                            originalname = g[1].Value;

                                            if (int.TryParse(g[5].Value, out int _yer))
                                                relased = _yer;
                                        }
                                        else
                                        {
                                            // Shingeki no Kyojin: The Final Season / Attack on Titan Final Season / Атака титанов. Последний сезон [TV-4] [2020(-2021)?,
                                            g = Regex.Match(title, "^([^/\\[\\(]+) / [^/\\[\\(]+ / ([^/\\[\\(]+) (\\[(ТВ|TV)-[0-9]+\\] )?\\[([0-9]{4})(-[0-9]{4})?,").Groups;
                                            if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[5].Value))
                                            {
                                                name = g[2].Value;
                                                originalname = g[1].Value;

                                                if (int.TryParse(g[5].Value, out int _yer))
                                                    relased = _yer;
                                            }
                                            else
                                            {
                                                // Shingeki no Kyojin: The Final Season / Атака титанов. Последний сезон [TV-4] [2020(-2021)?,
                                                g = Regex.Match(title, "^([^/\\[\\(]+) / ([^/\\[\\(]+) (\\[(ТВ|TV)-[0-9]+\\] )?\\[([0-9]{4})(-[0-9]{4})?,").Groups;
                                                if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value) && !string.IsNullOrWhiteSpace(g[5].Value))
                                                {
                                                    name = g[2].Value;
                                                    originalname = g[1].Value;

                                                    if (int.TryParse(g[5].Value, out int _yer))
                                                        relased = _yer;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    #endregion
                }
                else if (tracker is "731" or "733" or "1329" or "1330" or "1331" or "1332" or "1336" or "1337" or "1338" or "1339" or "658" or "232")
                {
                    #region Детям и родителям
                    if (!title.ToLower().Contains("pdf") && (row.Contains("должительность") || row.ToLower().Contains("мульт")))
                    {
                        // Академия монстров / Escuela de Miedo / Cranston Academy: Monster Zone (2020)
                        var g = Regex.Match(title, "^([^/\\(\\|]+) / [^/\\(\\|]+ / ([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                        if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
                        {
                            name = g[1].Value;
                            originalname = g[2].Value;

                            if (int.TryParse(g[3].Value, out int _yer))
                                relased = _yer;
                        }
                        else
                        {
                            // Трансформеры: Война за Кибертрон / Transformers: War For Cybertron (2020) 
                            g = Regex.Match(title, "^([^/\\(\\|]+) / ([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                            if (!string.IsNullOrWhiteSpace(g[1].Value) && !string.IsNullOrWhiteSpace(g[2].Value))
                            {
                                name = g[1].Value;
                                originalname = g[2].Value;

                                if (int.TryParse(g[3].Value, out int _yer))
                                    relased = _yer;
                            }
                            else
                            {
                                // Спина к спине (2020-2021) 
                                g = Regex.Match(title, "^([^/\\(\\|]+) \\(([0-9]{4})(-[0-9]{4})?\\)").Groups;
                                name = g[1].Value;

                                if (int.TryParse(g[2].Value, out int _yer))
                                    relased = _yer;
                            }
                        }
                    }
                    #endregion
                }
                #endregion

                if (string.IsNullOrWhiteSpace(name))
                    name = Regex.Split(title, "(\\[|\\/|\\(|\\|)", RegexOptions.IgnoreCase)[0].Trim();

                if (!string.IsNullOrWhiteSpace(name) || cats == null)
                {
                    #region types
                    string[] types = null;
                    switch (tracker)
                    {
                        case "270":
                        case "221":
                        case "882":
                        case "225":
                        case "227":
                        case "913":
                        case "218":
                        case "954":
                        case "1293":
                        case "1296":
                        case "1299":
                        case "682":
                        case "884":
                        case "693":
                            types = new string[] { "movie" };
                            break;
                        case "769":
                        case "768":
                            types = new string[] { "serial" };
                            break;
                        case "713":
                        case "576":
                        case "610":
                            types = new string[] { "docuserial", "documovie" };
                            break;
                        case "731":
                        case "733":
                        case "1329":
                        case "1330":
                        case "1331":
                        case "1332":
                        case "1336":
                        case "1337":
                        case "1338":
                        case "1339":
                            types = new string[] { "multfilm" };
                            break;
                        case "658":
                        case "232":
                            types = new string[] { "multserial" };
                            break;
                        case "623":
                        case "622":
                        case "621":
                        case "632":
                        case "627":
                        case "626":
                        case "625":
                        case "644":
                            types = new string[] { "anime" };
                            break;
                    }

                    if (types == null)
                        continue;

                    if (cats != null)
                    {
                        bool isok = false;
                        foreach (string cat in cats)
                        {
                            if (types.Contains(cat))
                                isok = true;
                        }

                        if (!isok)
                            continue;
                    }
                    #endregion

                    int.TryParse(_sid, out int sid);
                    int.TryParse(_pir, out int pir);

                    if (!validrq && !TorrentCache.Exists(TorrentFileMemKey(viewtopic)) && !TorrentCache.Exists(TorrentMagnetMemKey(viewtopic)))
                        continue;

                    torrents.Add(new TorrentDetails()
                    {
                        trackerName = "nnmclub",
                        types = types,
                        url = url,
                        title = title,
                        sid = sid,
                        pir = pir,
                        sizeName = sizeName,
                        parselink = $"{host}/nnmclub/parsemagnet?id={viewtopic}" + (!validrq ? "&usecache=true" : ""),
                        createTime = createTime,
                        name = name,
                        originalname = originalname,
                        relased = relased
                    });
                }
            }

            return true;
        }
        #endregion
    }
}
