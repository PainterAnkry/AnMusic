/*
 * AnMusic 内置音源插件：QQ 音乐（修正版）
 * 官方 v0.0 插件的 musicu.fcg DoSearchForQQMusicDesktop 现在强制 sign（2001），
 * 改用旧版客户端搜索 client_search_cp（免签名），
 * 播放地址走 CgiGetVkey 的 GET 免签名链路（仅非付费曲可播）。
 */
(function () {
    var UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';
    var HEADERS = {
        'user-agent': UA,
        referer: 'https://y.qq.com/',
        accept: 'application/json, text/plain, */*',
        cookie: 'uin=; pgv_pvid=9198273432'
    };
    var PAGE_SIZE = 20;

    function guid() {
        return (Array.from({ length: 32 }, function () {
            return Math.floor(Math.random() * 16).toString(16);
        }).join(''));
    }

    function formatSong(s) {
        var singers = (s.singer || []).map(function (x) { return x.name; }).filter(Boolean).join('、');
        var albumName = s.albumname || ((s.album || {}).name) || '';
        var albumMid = s.albummid || ((s.album || {}).mid) || '';
        var artwork = albumMid
            ? 'https://y.gtimg.cn/music/photo_new/T002R300x300M000' + albumMid + '.jpg'
            : '';
        return {
            id: String(s.songmid || s.mid || s.id || s.songid),
            songmid: s.songmid || s.mid,
            title: (s.songname || s.title || '').replace(/<[^>]+>/g, ''),
            artist: singers || '未知艺术家',
            album: albumName,
            artwork: artwork,
            duration: Number(s.interval || 0)
        };
    }

    async function search(keyword, page, type) {
        if (type && type !== 'music') return { isEnd: true, data: [] };
        var url = 'https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json'
            + '&n=' + PAGE_SIZE + '&p=' + page + '&w=' + encodeURIComponent(keyword);
        var res = await axios.get(url, { headers: HEADERS });
        var body = res && res.data;
        var list = body && body.data && body.data.song && body.data.song.list;
        if (!Array.isArray(list)) return { isEnd: true, data: [] };
        var songs = list.filter(function (s) {
            // 付费曲保留显示（与官方 App 一致），播放时若 vkey 为空会给出友好提示
            return !!(s.songmid || s.mid);
        }).map(formatSong);
        var total = body.data.song.total_song_num || body.data.song.totalnum || songs.length;
        return {
            isEnd: page * PAGE_SIZE >= total,
            data: songs
        };
    }

    async function getMediaSource(musicItem) {
        var mid = musicItem && (musicItem.songmid || musicItem.mid || musicItem.id);
        if (!mid) throw new Error('缺少 songmid');
        var g = guid();
        var req = {
            req_0: {
                module: 'vkey.GetVkeyServer',
                method: 'CgiGetVkey',
                param: {
                    guid: g,
                    songmid: [String(mid)],
                    songtype: [0],
                    uin: '0',
                    loginflag: 1,
                    platform: '20'
                }
            }
        };
        var url = 'https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&platform=yqq&data='
            + encodeURIComponent(JSON.stringify(req));
        var res = await axios.get(url, { headers: HEADERS });
        var d = res && res.data && res.data.req_0 && res.data.req_0.data;
        var info = d && d.midurlinfo && d.midurlinfo[0];
        var sip = d && d.sip;
        if (!info || !info.purl || !sip || !sip.length) {
            throw new Error('该曲目暂无可播放地址（可能为付费曲）');
        }
        var base = sip[0];
        return { url: base + info.purl };
    }

    module.exports = {
        platform: 'QQ音乐',
        version: '1.0.0-anmusic',
        author: 'AnMusic',
        search: search,
        getMediaSource: getMediaSource
    };
})();
