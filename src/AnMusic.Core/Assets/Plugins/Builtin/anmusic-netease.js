/*
 * AnMusic 内置音源插件：网易云音乐（修正版）
 * 适配点：官方 v0.0 插件的 weapi/cloudsearch 加密链路当前返回 50000005，
 * 改用仍然开放的 webapi 搜索（免加密）+ song/detail 批量补封面，
 * 播放走 music.163.com 外链（302 跳转真实音频，宿主自动跟随）。
 * 协议：MusicFree（platform / search / getMediaSource）
 */
(function () {
    var UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';
    var HEADERS = {
        'user-agent': UA,
        referer: 'https://music.163.com/',
        accept: 'application/json, text/plain, */*',
        cookie: 'appver=2.10.2; os=pc; __remember_enter=true'
    };
    var PAGE_SIZE = 30;

    function artistsOf(s) {
        var a = s.ar || s.artists || (s.album && s.album.artists) || [];
        return a.map(function (x) { return x.name; }).filter(Boolean).join('、');
    }

    function formatSong(s) {
        var album = s.al || s.album || {};
        return {
            id: String(s.id),
            title: s.name,
            artist: artistsOf(s) || '未知艺术家',
            album: album.name || '',
            artwork: album.picUrl ? String(album.picUrl).replace(/^http:/, 'https:') : '',
            duration: Math.round((s.dt || s.duration || 0) / 1000),
            fee: s.fee
        };
    }

    async function search(keyword, page, type) {
        if (type && type !== 'music') return { isEnd: true, data: [] };
        var offset = (page - 1) * PAGE_SIZE;
        var url = 'https://music.163.com/api/search/get?s=' + encodeURIComponent(keyword)
            + '&type=1&limit=' + PAGE_SIZE + '&offset=' + offset;
        var res = await axios.post(url, null, { headers: HEADERS });
        var body = res && res.data;
        if (!body || !body.result || !Array.isArray(body.result.songs)) {
            return { isEnd: true, data: [] };
        }
        var songs = body.result.songs
            .filter(function (s) {
                // fee: 0 免费 / 8 低清免费；1=VIP 外链 404，直接过滤避免点歌失败
                return s.fee === 0 || s.fee === 8;
            })
            .map(formatSong);

        // 搜索接口返回精简歌曲（无封面 URL），批量走 detail 补全
        try {
            var ids = songs.map(function (s) { return s.id; });
            if (ids.length) {
                var detailUrl = 'https://music.163.com/api/song/detail?ids='
                    + encodeURIComponent(JSON.stringify(ids.map(Number)));
                var detailRes = await axios.get(detailUrl, { headers: HEADERS });
                var details = (detailRes.data && detailRes.data.songs) || [];
                var byId = {};
                details.forEach(function (d) { byId[String(d.id)] = d; });
                songs.forEach(function (s) {
                    var d = byId[s.id];
                    if (d) {
                        var full = formatSong(d);
                        if (full.artwork) s.artwork = full.artwork;
                        if (!s.artist || s.artist === '未知艺术家') s.artist = full.artist;
                        if (full.album) s.album = full.album;
                        if (full.duration) s.duration = full.duration;
                    }
                });
            }
        } catch (e) { /* 封面补全失败不阻断搜索 */ }

        var total = body.result.songCount || songs.length;
        return {
            isEnd: offset + songs.length >= total,
            data: songs
        };
    }

    async function getMediaSource(musicItem) {
        var id = musicItem && musicItem.id;
        if (!id) throw new Error('缺少曲目 id');
        // 外链 302 到真实音频，宿主 HttpClient 自动跟随；fee=1(VIP) 在搜索阶段已过滤
        return { url: 'https://music.163.com/song/media/outer/url?id=' + encodeURIComponent(id) + '.mp3' };
    }

    module.exports = {
        platform: '网易云',
        version: '1.0.0-anmusic',
        author: 'AnMusic',
        search: search,
        getMediaSource: getMediaSource
    };
})();
