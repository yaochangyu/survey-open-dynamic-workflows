# OAuth2 Client Credentials Grant 於 C# (.NET 8 / 9 / 10) 的實作規格

> 狀態:草案 v1.0 ・ 日期:2026-10-11
> 驗證範圍:第 7 節完整範例已在 **.NET 8 SDK (net8.0)** 編譯通過(0 warning),並以 stub 驗證兩項行為:50 個並行請求只打 **1 次** token endpoint;下游回 401 時只刷新 **1 次** 並重送成功。net9.0 / net10.0 僅確認 SDK 存在,未逐一執行;各版本差異整理於第 8 節,屬依既有知識整理,未逐項查證官方文件,導入前請對照官方發行說明。

## 0. 摘要

| 議題 | 結論 |
|---|---|
| 規範 | RFC 6749 §4.4(+ §2.3 用戶端驗證、§5 回應/錯誤);Bearer 使用見 RFC 6750 |
| Token 取得 | 唯一 `POST` 至 token endpoint,`application/x-www-form-urlencoded`,`grant_type=client_credentials` |
| 快取 | 以 `expires_in` 計算到期,**提早 60 秒**刷新;buffer 不超過壽命的一半 |
| 並行 | 無鎖快路徑 + `SemaphoreSlim(1,1)` + double-check,同一時間只有一個請求打 IdP |
| 整合 | `IAccessTokenProvider`(Singleton,持有快取)+ `BearerTokenHandler`(`DelegatingHandler`,Transient)+ `IHttpClientFactory` |
| 錯誤處理 | token 端點:暫時性錯誤指數退避重試;`4xx`(`invalid_client` 等)不重試。下游 `401`:作廢該 token、刷新、重送一次 |

**不要**把快取放在 `DelegatingHandler` 內:handler 由 `IHttpClientFactory` 建立並約每 2 分鐘輪替(預設 `HandlerLifetime`),實例狀態會跟著被丟棄,也無法跨多個 HttpClient 共用。

---

## 1. RFC 6749 規範細節

### 1.1 適用情境
Client Credentials 用於 **機器對機器(M2M)**,client 以自己的身分(非代表使用者)存取資源。client 必須是 **confidential client**(RFC 6749 §4.4:"The client credentials grant type MUST only be used by confidential clients"),不可用於 SPA / 行動 App 等無法保管密鑰的環境。

### 1.2 Access Token Request(§4.4.2)

```http
POST /token HTTP/1.1
Host: idp.example.com
Authorization: Basic czZCaGRSa3F0MzpnWDFmQmF0M2JW
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&scope=orders.read+orders.write
```

| 參數 | 要求 | 說明 |
|---|---|---|
| `grant_type` | REQUIRED | 固定為 `client_credentials` |
| `scope` | OPTIONAL | 以空白分隔的字串,form-urlencode 後空白成為 `+`(或 `%20`) |
| `client_id` / `client_secret` | 視驗證方式 | 見 1.3 |

- 方法必須是 **POST**;body 為 `application/x-www-form-urlencoded`(非 JSON)。
- 必須使用 **TLS**(§3.2、§10.1)。
- 不需要 `redirect_uri`、`code`、使用者互動。

### 1.3 用戶端驗證(§2.3.1)

| 方式 | 規範立場 | 建議 |
|---|---|---|
| HTTP Basic:`Authorization: Basic base64(urlencode(id) ":" urlencode(secret))` | Authorization server **MUST 支援** | **預設採用** |
| Request body:`client_id`、`client_secret` 放 form | **MAY** 支援,**NOT RECOMMENDED**(secret 較易被日誌記錄) | 僅在 IdP 不支援 Basic 時使用 |

注意:id/secret 在 Base64 前要先做 form-urlencode(§2.3.1),本範例以 `Uri.EscapeDataString` 處理(空白會成 `%20` 而非 `+`,伺服器端解碼後一致;若某 IdP 對此挑剔,需改用 `+` 版本並實測)。

進階驗證(同為「用戶端驗證」,可取代共享密鑰):`private_key_jwt`(RFC 7523)、mTLS(RFC 8705)。高安全需求優先考慮,本文不展開。

### 1.4 成功回應(§4.4.3、§5.1)

```http
HTTP/1.1 200 OK
Content-Type: application/json;charset=UTF-8
Cache-Control: no-store
Pragma: no-cache

{ "access_token": "2YotnFZFEjr1zCsicMWpAA", "token_type": "Bearer", "expires_in": 3600, "scope": "orders.read" }
```

| 欄位 | 要求 | 實作注意 |
|---|---|---|
| `access_token` | REQUIRED | 視為不透明字串,**不要解析 JWT 來判斷到期**(以 `expires_in` 為準) |
| `token_type` | REQUIRED | 通常 `Bearer`(不分大小寫);下游若要求 `DPoP`/`MAC` 需另行處理 |
| `expires_in` | RECOMMENDED | 秒數;**可能缺漏**,需有 fallback 壽命。少數實作回傳字串,範例以 `JsonElement` 同時容忍數字與字串 |
| `scope` | OPTIONAL(核發範圍與請求不同時為 REQUIRED) | 可記錄供除錯 |
| `refresh_token` | **SHOULD NOT** 回傳(§4.4.3) | 本流程不需要 refresh token,過期就重新走一次 grant |

### 1.5 錯誤回應(§5.2)

HTTP 400(預設),`invalid_client` 且使用 Authorization 標頭驗證時為 **401** 並帶 `WWW-Authenticate`。

| `error` | 意義 | 可重試? |
|---|---|---|
| `invalid_request` | 缺參數、重複參數、格式錯 | 否(程式 bug) |
| `invalid_client` | 驗證失敗(id/secret 錯、未註冊) | 否(設定錯誤;重試會造成帳號鎖定風險) |
| `invalid_grant` | 此 grant 無效(client credentials 較少見) | 否 |
| `unauthorized_client` | 此 client 無權使用該 grant | 否 |
| `unsupported_grant_type` | IdP 不支援 | 否 |
| `invalid_scope` | scope 無效/超出權限 | 否 |

此外 IdP 的 `5xx`、`429`、網路逾時屬暫時性錯誤(規範之外的通用 HTTP 語意),可重試。

### 1.6 其他相關規範

- RFC 6750:以 `Authorization: Bearer <token>` 呼叫資源;資源伺服器拒絕時回 `401` + `WWW-Authenticate: Bearer error="invalid_token"`。
- RFC 8707:`resource` 參數(指定目標受眾);部分 IdP(如 Azure AD 用 `scope=api://.../.default`、Auth0 用 `audience`)以自家參數取代,需另增欄位。
- RFC 9700(OAuth 2.0 Security BCP):密鑰保管、最小 scope、短效 token。

---

## 2. Token 快取與過期緩衝

### 2.1 規則

```
lifetime   = expires_in（缺漏則用 FallbackLifetime，預設 5 分鐘）
buffer     = min(ExpiryBuffer(預設 60s), lifetime / 2)
refreshAt  = now + lifetime - buffer
可用       = now < refreshAt
```

- **為何提前**:涵蓋時鐘偏差、網路延遲、請求在途中到期。60 秒是常見預設;token 壽命很短(如 120 秒)時,`min(buffer, lifetime/2)` 避免每次請求都刷新。
- **時間來源**:注入 `TimeProvider`(.NET 8+),測試可用 `FakeTimeProvider`(`Microsoft.Extensions.TimeProvider.Testing`)快轉時間,不必 `Task.Delay`。
- **只用單調時間差?** 此處用 `GetUtcNow()` 以便與 IdP 時間語意一致;若在意系統時鐘被調整,可改存 `GetTimestamp()` 做 elapsed 計算。
- **主動作廢**:下游回 `401` 時呼叫 `Invalidate(rejectedToken)`;只有快取中仍是「被拒絕的那一個」才清除(compare-and-swap),避免 N 個並行 401 觸發 N 次刷新。
- **快取粒度**:key = (client_id, scope/audience)。多個 API / 多組 scope 時,每組一個 provider 實例(以具名 options + keyed service,見 6.2)。
- **記憶體 vs 分散式**:單一程序的 M2M 通常一個實例記憶體快取就夠。多執行個體(K8s 多 Pod)各自取 token 是常態且可接受,除非 IdP 對核發次數有嚴格配額,才考慮 `HybridCache`(.NET 9+)/ Redis 共享(需加密存放,且 token 等同密碼)。
- **背景預刷新**(可選):用 `PeriodicTimer` 在到期前刷新可讓請求路徑零等待,但增加複雜度與生命週期管理;一般建議 **lazy 刷新**即可。

---

## 3. 執行緒安全與防止並行重複請求

### 3.1 問題
快取過期瞬間若有 N 個請求同時進入,會產生 N 次 token 請求(thundering herd),可能觸發 IdP 限流。

### 3.2 方案:快路徑 + SemaphoreSlim + double-check

```csharp
if (IsFresh(_cached)) return _cached!.Value;            // 1. 無鎖快路徑(絕大多數請求在此返回)
await _gate.WaitAsync(ct);                              // 2. 只讓一個呼叫者去刷新
try {
    if (IsFresh(_cached)) return _cached!.Value;        // 3. 等鎖期間別人可能已刷新完成
    _cached = await RequestTokenAsync(ct);
    return _cached.Value;
} finally { _gate.Release(); }
```

要點:
- **不能用 `lock`**:`lock` 內不可 `await`。.NET 9 的 `System.Threading.Lock` 同樣不支援 async。非同步互斥用 `SemaphoreSlim(1,1)`。
- `_cached` 宣告 `volatile`,並使用**不可變**的 `CachedToken` record,讀取端永遠看到一致的 (token, 到期時間) 組合,不會看到半更新狀態。
- **取消語意**:呼叫者 A 持鎖刷新時被取消,鎖釋放後呼叫者 B 會接手重新刷新,不會連帶失敗。這是選用 `SemaphoreSlim` 而非「共用 `Lazy<Task>`」的理由(共用 Task 會讓 A 的取消/例外傳給所有等待者,需額外處理)。
- 刷新失敗時**不要**快取失敗結果;下一個呼叫者會再嘗試(由 token client 的重試策略吸收短暫故障)。若需保護 IdP,可加 circuit breaker(`AddResilienceHandler` 內加入)。
- 等鎖可用 `WaitAsync(timeout, ct)` 設上限,避免 IdP 卡死時所有請求無限堆疊;本範例依賴 token client 的 10 秒 timeout 間接限制。
- provider 必須是 **Singleton**。若註冊為 Transient/Scoped,每個實例各有一把鎖與一份快取,互斥失效。

---

## 4. 與 HttpClient / DelegatingHandler(IHttpClientFactory)整合

### 4.1 結構

```
                         ┌───────────────────────────────────────────────┐
 DemoApiClient(HttpClient)│ [外] 標準韌性管線 (重試/逾時/斷路)               │
        │                 │ [內] BearerTokenHandler  ← 附加/刷新 token       │
        ▼                 │ [底] SocketsHttpHandler                        │
   呼叫下游 API           └───────────────────────────────────────────────┘
                                   │ GetTokenAsync()
                                   ▼
              ClientCredentialsTokenProvider (Singleton: 快取 + SemaphoreSlim)
                                   │ CreateClient("oauth2-token")
                                   ▼
              token 專用 HttpClient(**不掛** BearerTokenHandler;自有重試/逾時)
```

### 4.2 設計決策

1. **Handler 薄、Provider 厚**:handler 只負責「取 token → 設標頭 → 遇 401 換新重送」。
2. **Handler 註冊為 Transient**(`AddTransient<BearerTokenHandler>()` + `AddHttpMessageHandler<T>()`);它不持有狀態,狀態在 Singleton provider。
3. **token 專用 HttpClient 與業務 HttpClient 分離**:token client 若也掛 `BearerTokenHandler` 會無限遞迴(取 token 需要 token)。
4. **管線順序 = 註冊順序,先註冊者在外層**。建議 **韌性在外、驗證在內**:每次重試都重新經過 `BearerTokenHandler`,因此重試時拿到的是當下有效的 token。若顛倒,重試會重用已過期的標頭。
5. **401 重送只做一次**,且需 **可重播 body**:`HttpRequestMessage` 不能重送同一實例(`InvalidOperationException`),所以範例先緩衝 content 為 `byte[]`,重送時 clone request。**串流/大型上傳不適用**此緩衝,此時改為在送出前確保 token 新鮮(拉長 buffer),並讓 401 往上拋給呼叫端決定。
6. **不要把 `Authorization` 設在 `DefaultRequestHeaders`**:token 會過期,固定值會失效,且 HttpClient 實例被共用。
7. **不要 `new HttpClient()`**:會有 socket 耗盡與 DNS 不更新問題;一律走 `IHttpClientFactory`。

### 4.3 註冊(摘自第 7 節)

```csharp
services.AddOAuth2ClientCredentials("OAuth2:Api");   // options 驗證 + provider + token client

var api = services.AddHttpClient<DemoApiClient>(c => c.BaseAddress = new Uri("https://api.example.com/"));
api.AddStandardResilienceHandler();                  // 外層
api.AddHttpMessageHandler<BearerTokenHandler>();     // 內層
```

`appsettings.json`(密鑰**不要**放這裡,見第 6 節):

```json
{ "OAuth2": { "Api": {
    "TokenEndpoint": "https://idp.example.com/oauth2/token",
    "ClientId": "my-service",
    "Scope": "orders.read orders.write"
} } }
```

---

## 5. 錯誤處理與重試

### 5.1 兩層分離

| 層 | 對象 | 策略 |
|---|---|---|
| A. token endpoint | 取得 token 的 POST | 暫時性錯誤重試 3 次,指數退避 + jitter,單次逾時 10 秒;`4xx` 不重試 |
| B. 下游 API | 業務請求 | `AddStandardResilienceHandler()`(速率限制、總逾時、重試、斷路器、單次逾時);另加「401 → 刷新 → 重送一次」 |

### 5.2 Token endpoint 重試判斷

| 狀況 | 重試 | 例外類型 |
|---|---|---|
| `HttpRequestException`(連線失敗、DNS) | 是 | `TokenRequestException(IsTransient: true)` |
| `408` / `429` / `5xx` | 是(`429` 尊重 `Retry-After`,`HttpRetryStrategyOptions` 預設會讀取) | 同上 |
| `400 invalid_request / invalid_scope / unsupported_grant_type` | 否 | `IsTransient: false` |
| `400/401 invalid_client`、`unauthorized_client` | **否**(設定錯誤,重試可能鎖帳號) | `IsTransient: false` |
| 200 但缺 `access_token` | 否 | `invalid_response` |

> 取 token 的 POST 雖是 POST,但本質上是冪等的(只是核發新 token),因此可安全重送。若改用 `AddStandardResilienceHandler`,需留意預設對非冪等方法的處理,所以範例用 `AddResilienceHandler` 自訂策略。

### 5.3 下游 401 處理

1. 取得 token T1,送出,收到 `401`。
2. `Invalidate(T1)`:僅當快取仍是 T1 才清除。
3. `GetTokenAsync()`:若別的請求已刷新則直接取得 T2,否則由持鎖者刷新。
4. 以 T2 重送**一次**;仍 `401` 就把回應交給呼叫端(通常是權限/受眾問題,再試無益)。

`403` 表示已驗證但無權限,**不要**重試或刷新 token。

### 5.4 例外與日誌

- 對外拋 `TokenRequestException`(含 `StatusCode`、`Error`、`ErrorDescription`、`IsTransient`),上層可據此決定告警或降級。
- **日誌絕不輸出** `client_secret`、`access_token`、`Authorization` 標頭。HttpClient 預設的 `System.Net.Http.HttpClient` 日誌不含標頭值,但自訂 logging handler 或 `HttpClientLogging` 的 header 白名單需檢查(預設 `Authorization` 會被遮蔽,勿加入白名單)。
- 可記錄:token 取得耗時、`expires_in`、刷新次數、錯誤碼(`error` 欄位)。

---

## 6. 安全與設定

### 6.1 密鑰管理
- 開發:`dotnet user-secrets`;正式:Azure Key Vault / AWS Secrets Manager / K8s Secret,以環境變數或設定提供者注入(`OAuth2__Api__ClientSecret`)。**禁止進版控。**
- 優先考慮免密鑰方案:Azure Managed Identity / Workload Identity Federation、`private_key_jwt`、mTLS。
- 最小 scope;密鑰可輪替(支援新舊並存期)。
- `ValidateDataAnnotations().ValidateOnStart()`:缺設定時**啟動即失敗**,而非第一個請求才爆。
- token endpoint 強制 `https`(可在 options 驗證加上 `Uri.Scheme == "https"` 檢查)。

### 6.2 多組憑證 / 多個下游
同一服務要呼叫多個使用不同 scope 的 API 時:
- 使用具名 options(`IOptionsMonitor<T>.Get(name)`)+ .NET 8 keyed services(`AddKeyedSingleton<IAccessTokenProvider>(name, ...)`),每組一個 provider 與一把鎖;
- 為每個 handler 傳入對應的 provider(`ActivatorUtilities` 或工廠註冊)。
- 範例預設單組,擴充時**不要**共用同一個 provider 去服務不同 scope(快取會互相覆蓋)。

---

## 7. 參考實作(已於 net8.0 編譯驗證)

```csharp
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace OAuth2Sample;

public enum ClientAuthStyle { Basic, RequestBody }

public sealed class OAuth2ClientCredentialsOptions
{
    [Required] public Uri TokenEndpoint { get; set; } = default!;
    [Required] public string ClientId { get; set; } = "";
    [Required] public string ClientSecret { get; set; } = "";
    public string? Scope { get; set; }
    public ClientAuthStyle AuthStyle { get; set; } = ClientAuthStyle.Basic;
    public TimeSpan ExpiryBuffer { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan FallbackLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class TokenRequestException(
    HttpStatusCode statusCode, string? error, string? description, bool isTransient, Exception? inner = null)
    : Exception($"Token request failed: {(int)statusCode} {error} {description}".TrimEnd(), inner)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Error { get; } = error;
    public string? ErrorDescription { get; } = description;
    public bool IsTransient { get; } = isTransient;
}

public interface IAccessTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken ct = default);
    /// <summary>僅當快取中的 token 仍等於被拒絕的 token 時才清除,避免並行 401 重複刷新。</summary>
    void Invalidate(string rejectedToken);
}

public sealed class ClientCredentialsTokenProvider(
    IHttpClientFactory httpFactory,
    IOptions<OAuth2ClientCredentialsOptions> options,
    TimeProvider time) : IAccessTokenProvider, IDisposable
{
    public const string HttpClientName = "oauth2-token";

    private sealed record CachedToken(string Value, DateTimeOffset RefreshAt);
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] JsonElement ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private readonly OAuth2ClientCredentialsOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile CachedToken? _cached;

    public async ValueTask<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (IsFresh(_cached)) return _cached!.Value;          // 快路徑:無鎖

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(_cached)) return _cached!.Value;     // double-check:等鎖期間別人可能已刷新
            var fresh = await RequestTokenAsync(ct).ConfigureAwait(false);
            _cached = fresh;
            return fresh.Value;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate(string rejectedToken)
    {
        var c = _cached;
        if (c is not null && c.Value == rejectedToken)
            Interlocked.CompareExchange(ref _cached, null, c);
    }

    private bool IsFresh(CachedToken? t) => t is not null && time.GetUtcNow() < t.RefreshAt;

    private async Task<CachedToken> RequestTokenAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _o.TokenEndpoint);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (!string.IsNullOrEmpty(_o.Scope)) form["scope"] = _o.Scope;

        if (_o.AuthStyle == ClientAuthStyle.Basic)
        {
            // RFC 6749 §2.3.1:先以 form-urlencoded 編碼 id / secret,再 Base64
            var raw = $"{Uri.EscapeDataString(_o.ClientId)}:{Uri.EscapeDataString(_o.ClientSecret)}";
            req.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        else
        {
            form["client_id"] = _o.ClientId;
            form["client_secret"] = _o.ClientSecret;
        }
        req.Content = new FormUrlEncodedContent(form);

        var http = httpFactory.CreateClient(HttpClientName);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex)
        {
            throw new TokenRequestException(HttpStatusCode.ServiceUnavailable, null, ex.Message, true, ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            TokenResponse? tr = null;
            try { tr = JsonSerializer.Deserialize<TokenResponse>(body); } catch (JsonException) { }

            if (!resp.IsSuccessStatusCode)
            {
                var transient = (int)resp.StatusCode >= 500
                    || resp.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                throw new TokenRequestException(resp.StatusCode, tr?.Error, tr?.ErrorDescription, transient);
            }

            if (string.IsNullOrEmpty(tr?.AccessToken))
                throw new TokenRequestException(resp.StatusCode, "invalid_response", "missing access_token", false);

            var lifetime = ParseLifetime(tr!.ExpiresIn) ?? _o.FallbackLifetime;
            // buffer 不得超過一半壽命,避免短效 token 每次請求都刷新
            var buffer = TimeSpan.FromTicks(Math.Min(_o.ExpiryBuffer.Ticks, lifetime.Ticks / 2));
            return new CachedToken(tr.AccessToken!, time.GetUtcNow() + lifetime - buffer);
        }
    }

    private static TimeSpan? ParseLifetime(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var n) => TimeSpan.FromSeconds(n),
        JsonValueKind.String when long.TryParse(e.GetString(), out var n) => TimeSpan.FromSeconds(n),
        _ => null
    };

    public void Dispose() => _gate.Dispose();
}

public sealed class BearerTokenHandler(IAccessTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // 401 重送需要可重播的 body,故先緩衝
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var token = await tokens.GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new("Bearer", token);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        // token 可能被提前撤銷:作廢後換新 token 重送一次(僅一次)
        response.Dispose();
        tokens.Invalidate(token);
        var newToken = await tokens.GetTokenAsync(ct).ConfigureAwait(false);

        using var retry = Clone(request, body);
        retry.Headers.Authorization = new("Bearer", newToken);
        return await base.SendAsync(retry, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage src, byte[]? body)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri) { Version = src.Version, VersionPolicy = src.VersionPolicy };
        foreach (var h in src.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        foreach (var o in src.Options) clone.Options.TryAdd(o.Key, o.Value);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var h in src.Content!.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return clone;
    }
}

public static class OAuth2Extensions
{
    public static IServiceCollection AddOAuth2ClientCredentials(this IServiceCollection services, string configSection)
    {
        services.AddOptions<OAuth2ClientCredentialsOptions>()
            .BindConfiguration(configSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAccessTokenProvider, ClientCredentialsTokenProvider>();
        services.AddTransient<BearerTokenHandler>();

        // token 端點專用 client:不掛 BearerTokenHandler(避免遞迴),自訂重試(POST 取 token 可安全重送)
        services.AddHttpClient(ClientCredentialsTokenProvider.HttpClientName)
            .AddResilienceHandler("oauth2-token", b => b
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromMilliseconds(500),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException
                        || args.Outcome.Result is { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError })
                })
                .AddTimeout(TimeSpan.FromSeconds(10)));
        return services;
    }
}

public sealed class DemoApiClient(HttpClient http)
{
    public Task<string> GetAsync(CancellationToken ct = default) => http.GetStringAsync("ping", ct);
}

public static class DemoRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddOAuth2ClientCredentials("OAuth2:Api");
        var api = services.AddHttpClient<DemoApiClient>(c => c.BaseAddress = new Uri("https://api.example.com/"));
        api.AddStandardResilienceHandler();            // 外層:每次重試都會重新經過內層 BearerTokenHandler
        api.AddHttpMessageHandler<BearerTokenHandler>(); // 內層:附加 / 刷新 token
    }
}
```

---

## 8. 版本差異(.NET 8 / 9 / 10)

| 項目 | .NET 8 (LTS) | .NET 9 (STS) | .NET 10 (LTS) |
|---|---|---|---|
| `TimeProvider`、keyed services | 引入,可用 | 可用 | 可用 |
| `Microsoft.Extensions.Http.Resilience` | 8.x | 9.x | 10.x(隨主版本更新) |
| `HybridCache`(分散式共享 token 時) | 以套件形式(preview/後續) | 正式提供 | 可用 |
| `System.Threading.Lock` | 無 | 有(僅同步,不能 await) | 有 |
| 本文程式碼 | 已編譯並執行驗證 | 無需修改(預期) | 無需修改(預期) |

- 以上除 net8.0 的編譯執行結果外,其餘為依既有知識整理,**未逐項查證官方文件**;升版時請以官方 release notes 與 breaking changes 為準。
- 僅需調整 `TargetFramework` 與對應套件主版本號;範例未使用任何版本專屬 API。
- 支援期限:請查 Microsoft 官方 .NET 支援政策頁;選型時 LTS 優先。

## 9. 現成函式庫 vs 自行實作

| 選項 | 適用 | 備註 |
|---|---|---|
| 本文自行實作 | 單一 IdP、需求單純、希望零依賴 | 約 200 行,可控 |
| `Duende.AccessTokenManagement` | 需要完整 token 管理(多 client、DPoP、分散式快取) | 商業授權條款需自行確認 |
| `Microsoft.Identity.Client`(MSAL)`ConfidentialClientApplication` | Azure AD / Entra ID | 內建快取、自動刷新,Entra 場景**優先使用** |
| `Azure.Identity` `ClientSecretCredential` | Azure SDK 生態 | 同上 |
| `IdentityModel` 的 `RequestClientCredentialsTokenAsync` | 只想省略表單組裝 | 不含快取 |

選型建議:IdP 是 Entra ID 就用 MSAL / Azure.Identity;其他標準 OAuth2 IdP 且需求如本文,用自行實作;需要進階功能再評估 Duende。

## 10. 測試清單

1. **並行**:N=50~100 個並行 `GetTokenAsync`,斷言 token endpoint 只被呼叫 1 次(已驗證:50 → 1)。
2. **提前刷新**:`FakeTimeProvider` 前進 `expires_in - 60s` 之前仍用快取、之後觸發刷新。
3. **短效 token**:`expires_in=90`,確認 buffer 被限制為 45 秒,不會每次都刷新。
4. **缺 `expires_in`**:使用 `FallbackLifetime`。
5. **401 重送**:下游第一次回 401,確認只刷新一次並成功(已驗證);並行 401 不造成多次刷新。
6. **POST body 重送**:帶 JSON body 的請求在 401 後重送內容一致。
7. **錯誤**:`invalid_client` 不重試;`503` 重試後成功;`429` 帶 `Retry-After`。
8. **取消**:持鎖者被取消後,下一個等待者能完成刷新。
9. **機密外洩**:日誌與例外訊息不含 secret / token。
10. **啟動驗證**:缺 `ClientSecret` 時應用程式啟動失敗。

## 11. 已知限制與待確認事項

- 範例的 `Clone` 僅複製 method、URI、version、headers、options 與 body;自訂 `HttpContent` 子類別(串流、`MultipartContent` 中的串流)不支援重送。
- 未實作 `resource`/`audience` 參數、DPoP、`private_key_jwt`、mTLS;需要時於 `RequestTokenAsync` 擴充。
- 未實作背景預刷新與分散式共享快取。
- 不同 IdP 在 `expires_in` 型別、scope 分隔、`audience` 參數上有差異,導入前以目標 IdP 實測。
- `Uri.EscapeDataString` 與嚴格 `application/x-www-form-urlencoded` 編碼(空白為 `+`)有細微差異,對含特殊字元的 secret 請實測。

## 12. 參考

- RFC 6749 The OAuth 2.0 Authorization Framework(§2.3、§4.4、§5)
- RFC 6750 Bearer Token Usage
- RFC 7523 JWT Profile for Client Authentication / RFC 8705 mTLS / RFC 8707 Resource Indicators
- RFC 9700 Best Current Practice for OAuth 2.0 Security
- Microsoft Learn:IHttpClientFactory、DelegatingHandler、Build resilient HTTP apps(`Microsoft.Extensions.Http.Resilience`)、TimeProvider
