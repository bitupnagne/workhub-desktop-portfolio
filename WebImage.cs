using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Portfolio.OfficeSystem
{
    // WPF Image는 절대 URL을 만나면 시스템 WebRequest로 받아서 LAN IP / 프록시 환경에서 자주 실패한다.
    // 같은 HttpClient(쿠키 컨테이너 공유)로 받아 StreamSource로 주입하여 환경 의존을 제거한다.
    //
    // [B-7] 메모리 캐시 — 같은 사진 URL을 반복 다운로드하지 않는다.
    //  - 캐시 키: URL에서 `?cb=...` 쿼리스트링 제외한 경로 부분 (캐시 버스터는 강제 무효화용)
    //  - 동시에 같은 URL을 여러 Image가 요청해도 다운로드는 1회만 수행 (in-flight Task 공유)
    //  - LRU 크기 제한 200개
    public static class WebImage
    {
        private static HttpClient? _client;
        public static void Configure(HttpClient client) => _client = client;

        private const int CacheMaxEntries = 200;
        private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new();
        private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inflight = new();
        private static readonly LinkedList<string> _lruOrder = new();
        private static readonly object _lruLock = new();

        public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
            "Url", typeof(string), typeof(WebImage),
            new PropertyMetadata(null, OnUrlChanged));

        public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);
        public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);

        public static void ClearCache()
        {
            _cache.Clear();
            _inflight.Clear();
            lock (_lruLock) { _lruOrder.Clear(); }
        }

        private static string NormalizeCacheKey(string url)
        {
            // cb는 프로필 사진 강제 갱신용이므로 캐시 키에 포함한다.
            if (url.Contains("cb=", StringComparison.OrdinalIgnoreCase)) return url;
            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) : url;
        }

        private static void TouchLru(string key)
        {
            lock (_lruLock)
            {
                _lruOrder.Remove(key);
                _lruOrder.AddFirst(key);
                while (_lruOrder.Count > CacheMaxEntries)
                {
                    var oldest = _lruOrder.Last!.Value;
                    _lruOrder.RemoveLast();
                    _cache.TryRemove(oldest, out _);
                }
            }
        }

        private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image img) return;
            var url = e.NewValue as string;
            if (string.IsNullOrEmpty(url) || url == "about:blank")
            {
                img.Source = null;
                return;
            }
            if (_client == null) { img.Source = null; return; }

            string key = NormalizeCacheKey(url);

            if (_cache.TryGetValue(key, out var cached))
            {
                if ((img.GetValue(UrlProperty) as string) == url) img.Source = cached;
                TouchLru(key);
                return;
            }

            // 동시에 같은 URL을 요청하면 한 번만 다운로드
            var bmp = await _inflight.GetOrAdd(key, _ => DownloadAsync(url)).ConfigureAwait(true);

            if (bmp != null)
            {
                _cache[key] = bmp;
                TouchLru(key);
            }
            _inflight.TryRemove(key, out _);

            if ((img.GetValue(UrlProperty) as string) == url) img.Source = bmp;
        }

        private static async Task<BitmapImage?> DownloadAsync(string url)
        {
            try
            {
                var bytes = await _client!.GetByteArrayAsync(url);
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
