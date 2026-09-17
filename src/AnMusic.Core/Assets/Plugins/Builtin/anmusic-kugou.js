/*
 * AnMusic 内置音源插件：酷狗音乐（修正版）
 * 官方 v0.0 插件 privilege===0/8 过滤规则已失效（当前免费曲为 8、付费曲为 10），
 * 且响应恒为 gzip（宿主已开自动解压）。播放走 trackercdnbj v2 免签名链路。
 */
(function () {
    var CryptoJS = require('crypto-js');
    var UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';
    var PAGE_SIZE = 20;

    function formatSong(s) {
        return {
            id: String(s.hash),
            hash: s.hash,
            album_id: s.album_id,
            album_audio_id: s.album_audio_id,
            title: s.songname || s.song_name || '未知标题',
            artist: s.singername || (s.singer_name || '').replace(/、/g, '、') || '未知艺术家',
            album: s.album_name || '',
            artwork: s.image || s.trans_param && s.trans_param.union_cover || s.album_sizable_cover || '',
            duration: Number(s.duration || 0)
        };
    }

    async function search(keyword, page, type) {
        if (type && type !== 'music') return { isEnd: true, data: [] };
        var url = 'http://mobilecdn.kugou.com/api/v3/search/song?format=json'
            + '&keyword=' + encodeURIComponent(keyword)
            + '&page=' + page + '&pagesize=' + PAGE_SIZE + '&showtype=1';
        var res = await axios.get(url, { headers: { 'user-agent': UA } });
        var body = res && res.data;
        var info = body && body.data && body.data.info;
        if (!Array.isArray(info)) return { isEnd: true, data: [] };
        // 0/8 可通过 tracker 直链播放；10 等为会员曲（tracker status=2）
        var songs = info.filter(function (s) {
            return s.hash && (s.privilege === 0 || s.privilege === 8);
        }).map(formatSong);
        var total = (body.data && body.data.total) || songs.length;
        return {
            isEnd: page * PAGE_SIZE >= total,
            data: songs
        };
    }

    async function getMediaSource(musicItem) {
        var hash = musicItem && (musicItem.hash || musicItem.id);
        if (!hash) throw new Error('缺少歌曲 hash');
        var key = CryptoJS.MD5(String(hash).toLowerCase() + 'kgcloudv2').toString();
        var url = 'http://trackercdnbj.kugou.com/i/v2/?cmd=23&hash=' + encodeURIComponent(hash)
            + '&key=' + key + '&pid=1&behavior=play&version=8990';
        var res = await axios.get(url, { headers: { 'user-agent': UA } });
        var d = res && res.data;
        if (!d || d.status !== 1 || !d.url) {
            // status=2 通常为会员/无版权曲
            throw new Error('酷狗暂未返回可播放地址（可能为会员曲）');
        }
        var mediaUrl = Array.isArray(d.url) ? d.url[0] : d.url;
        if (d.extName && /\.\w+$/.test(mediaUrl) === false) mediaUrl += '.' + d.extName;
        return { url: mediaUrl, headers: {} };
    }

    module.exports = {
        platform: '酷狗',
        version: '1.0.0-anmusic',
        author: 'AnMusic',
        search: search,
        getMediaSource: getMediaSource
    };
})();
