/*
 * AnMusic 内置音源插件：酷我音乐（修正版）
 * 官方 v0.0 插件的 www.kuwo.cn/api 接口现在强制 Hm_Iuvt Cookie + Secret 反爬，
 * 改用移动端免签名搜索接口与 antiserver convert_url3 播放链路。
 */
(function () {
    var UA = 'Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1';
    var PAGE_SIZE = 20;

    function ridOf(item) {
        var rid = item.MUSICRID || String(item.id || item.rid || '');
        return String(rid).replace(/^MUSIC_/, '');
    }

    function formatSong(item) {
        var picShort = item.web_albumpic_short || item.albumpic_short || '';
        var artwork = '';
        if (/^https?:/i.test(picShort)) artwork = picShort;
        else if (picShort) artwork = 'https://img1.kuwo.cn/star/albumcover/' + picShort;
        return {
            id: ridOf(item),
            title: (item.SONGNAME || item.NAME || item.songName || '未知标题').replace(/&nbsp;/g, ' '),
            artist: (item.ARTIST || item.FARTIST || item.artist || '未知艺术家').replace(/&nbsp;/g, ' '),
            album: (item.ALBUM || item.album || '').replace(/&nbsp;/g, ' '),
            artwork: artwork,
            duration: Number(item.DURATION || item.duration || 0)
        };
    }

    async function search(keyword, page, type) {
        if (type && type !== 'music') return { isEnd: true, data: [] };
        var pn = Math.max(0, page - 1); // 该接口页码从 0 开始
        var url = 'https://kuwo.cn/search/searchMusicBykeyWord?vipver=1&client=kt&ft=music'
            + '&cluster=0&strategy=2012&encoding=utf8&rformat=json&mobi=1'
            + '&issubtitle=1&show_copyright_off=1&pn=' + pn + '&rn=' + PAGE_SIZE
            + '&all=' + encodeURIComponent(keyword);
        var res = await axios.get(url, { headers: { 'user-agent': UA } });
        var body = res && res.data;
        var list = body && body.abslist;
        if (!Array.isArray(list)) return { isEnd: true, data: [] };
        var songs = list.filter(function (x) { return ridOf(x); }).map(formatSong);
        var total = Number(body.TOTAL || body.total || songs.length);
        return {
            isEnd: (pn + 1) * PAGE_SIZE >= total,
            data: songs
        };
    }

    async function getMediaSource(musicItem) {
        var rid = musicItem && (musicItem.id || musicItem.rid);
        if (!rid) throw new Error('缺少曲目 rid');
        var url = 'http://antiserver.kuwo.cn/anti.s?type=convert_url3&rid='
            + encodeURIComponent(String(rid).replace(/^MUSIC_/, ''))
            + '&format=mp3&response=url';
        var res = await axios.get(url, { headers: { 'user-agent': UA } });
        var d = res && res.data;
        if (d && typeof d === 'object' && d.url) return { url: d.url };
        // response=url 时部分版本直接返回纯文本地址
        if (typeof d === 'string' && /^https?:\/\//i.test(d.trim())) return { url: d.trim() };
        throw new Error('酷我暂未返回可播放地址');
    }

    module.exports = {
        platform: '酷我',
        version: '1.0.0-anmusic',
        author: 'AnMusic',
        search: search,
        getMediaSource: getMediaSource
    };
})();
