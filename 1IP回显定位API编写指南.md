网络综合查询器X 

# IP 回显/定位 API 编写指南

查询器X（及开发者，简称”我们“）自 11.2609.1.0 版本起，不再内置 API 接口，仅保留几个必要的 IP 回显 API，确保程序首次使用时可显示本机外网 IP。位置 API 则只保留了本地数据库。因此，我们新增了 API 配置文件功能，你可自行编辑配置，打造私人 API 列表，让 API 使用更符合你的心意。

如你需使用其他自行收集/自购的 API 来回显你的 IP 地址 / IP 地理位置，阅读本文后可自行编写。也可将本文交给 AI 阅读，让 AI 帮你编写。

截至11.2609.1.0版本，IP 回显/定位 支持以下 API 配置文件：

- `NICX_Api1.nicxapi`：公网 IPv4/IPv6 回显接口（API1）。
- `NICX_Api2.nicxapi`：指定 IP 的地理位置等信息查询接口（API2）。

配置文件使用查询器X的专用语法，本文详细介绍。我们的语法非常接近C#，可读性较高，提供的方法可基本满足绝大多数在线API使用。

隐私问题？不用担心，我们不会收集你的 API 配置文件。除非你主动分享，否则其他人也无法获取你的配置文件，它永远是本地的、私密的。选择权完全在你——用不用都听你的。

## 零、AI  编写建议

考虑到大家可能更愿意用 AI 编写配置文件，可[点击这里]()下载本 Wiki 完整文档。下载后，把`本文`和`API 的官方文档`一起交给 AI，同时提供你的想法让 AI 编写。

如 API 官方没有文档，则向 AI 提供你 API 的使用方法，包括但不限于：

- 接口 URL（特别对于地理位置接口需说明目标 IP/Key/Token 等参数在 URL 中的位置）
- 接口访问方式（GET/POST/是否附带Header/Cookie等）
- IPv4、IPv6 支持情况（对于回显接口/国内优先接口）
- 一份成功响应示范
- 希望 Loc（信息1） 和 AS（信息2） 分别显示哪些字段
- 等等

提示：不要向不可信的 AI 提供真实私人 Key。可先用其他字符占位，生成后在本机替换；配置文件只能使用本文提到的函数、方法等，未明确写出的均为不支持，无法使用。目前已有函数方法足够满足绝大多数 API 的使用。

## 一、快速开始

1. 将配置文件放在查询器X主程序（NetInfoCheckerX.exe）旁边，再启动程序（建议使用 UTF-8 编码）。

   如你是临时启动，配置文件可能会在退出后自动被删除。建议使用本地启动模式运行程序。

2. 确认文件名：
   - `NICX_Api1.nicxapi`
   - `NICX_Api2.nicxapi`

   可只使用其中一个配置文件，也可都使用，或都不使用。

3. 修改文件后，需重新启动查询器X重载。

4. 文件不存在时，程序只加载内置接口和本地IP地理位置库。

5. 文件存在但有语法错误，该文件放弃加载（不会只加载正确部分）。错误详情会写在主程序旁：

- `NICX_Api1.error.log`
- `NICX_Api2.error.log`

日志会指出错误所在行和附近内容，修正配置后重启以重载。

提示：避免将包含私人 Key 的配置文件公开上传或分享给他人。

## 二、基本语法

### 2.1 文件版本

两个文件的第一条有效语句是文件版本。目前只支持版本 `1`。

```
nicxapi 1;
```

### 2.2 注释

使用 `//` 编写单行注释，目前不支持 `/* ... */` 多行注释。

```
// 这一行不会被程序执行
```

### 2.3 字符串和转义

```
"普通字符串"
123
true
false
null
```

普通字符串支持以下转义：

| 写法 | 含义 |
| --- | --- |
| `\n` | 换行 |
| `\r` | 回车 |
| `\t` | 制表符 |
| `\\` | 反斜杠 |
| `\"` | 双引号 |

长篇文本（如鼠标提示）可使用三引号：

```
tooltip IPCN = """
1.公开接口A  2.公开接口B
修改配置后请重启程序。
""";
```

### 2.4 表达式

表达式可是字符串、整数、布尔值、变量或函数调用，并且允许嵌套：

```
let url = Concat("https://example.com/query?ip=", ip);
let value = Trim(GetJson(json, "data.ip"));
let failed = Or(IsEmpty(json), HasPrefix(json, "error"));
```

当前语法没有 `+`、`==`、`&&`、`||` 等运算符。请使用 `Concat`、`Equals`、`And`、`Or` 等函数。函数名区分大小写。

### 2.5 鼠标提示（tooltip）

鼠标放置在对应的下拉框时，自动显示编写的鼠标提示。可用提示如下：

| 文件 | 分组 | 对应位置 |
| --- | --- | --- |
| API1 | `IPCN` | 国内方向 IP 回显接口 |
| API1 | `IPCNYX` | 国内双栈优先测试接口 |
| API1 | `IPGFW` | 国外方向 IP 回显接口 |
| API2 | `GEOCN` | 国内方向使用的位置接口 |
| API2 | `GEOGFW` | 国外方向使用的位置接口 |

鼠标提示没有格式限制，可任意输入文本。但建议设置为实际添加的接口 ID 及其对应的名字，以便查阅。例：

```
tooltip GEOCN = """
1.本地库  2.CZ88  3.IP138
""";
```

提示文字不会自动生成。增删接口或调整 ID 后，你需自行同步修改相应提示。

## 三、Provider (接口)

API1、API2均需要使用到 Provider 。在查询器X中，一个 Provider 就是一个接口。Provider 的 `ID` 同时用于下拉框显示和接口排序。例：

```
provider IPCN 3
{
    // 在这里写入接口访问规则
}
```

该选项会在`国内方向IP回显接口`下拉框中显示为 `3`。由于下拉框空间所限，接口名称不设置单独显示。建议在对应的鼠标提示（本例为`tooltip GEOCN`）中说明 `3` 代表哪一家的接口。

注意：

- 同一分组内不要重复使用 ID。
- 程序内置项与外置项 ID 冲突时，优先保留内置项，跳过外置项。
- 建议为新接口选择程序内尚未使用的 ID（可先查看程序已内置多少接口。后续内置接口数量可能会小幅调整，不另行通知，以实际为准）。
- 主程序支持记忆下拉框当前选中项（右键“接口”二字），但记忆的是次序而不是名字。故增删接口或修改顺序后，旧的记忆位置可能对应到其他接口，使用时请注意。
- 接口 ID 不强制连号和顺序。例：可设置 ID 1~5 作为公网出口接口，但为了分组便利，随后立刻使用 20~22 为教育网出口接口，中间 6~19 号可以空号—— 一切由你安排。

## 四、API1：公网 IP 回显 / 双栈优先测试接口

API1 方法同时用于公网 IP 回显 / 双栈优先测试接口。因此，每个 Provider 可以有两种使用方法：

IP 地址回显使用：Provider 内部分别定义 IPv4 和 IPv6 回显接口，返回的文本将直接填充到对应显示区域。请确保每个 API 只返回对应 IP（如 IPv4 部分使用的 API 只会返回 IPv4地址，不会窜入 IPv6 地址，反之亦然），确保使用体验。

双栈优先测试使用：Provider 只定义 IPv4，不定义 IPv6。定义 IPv4 时，在内部接口使用支持双栈访问的API域名，并添加到优先接口`IPCNYX`中（即形如 `provider IPCNYX 4`）。

或者，你也可按喜欢的方法来（例如某API只有IPv4，没有IPv6）。查询器X不设任何限制。

### 4.1 基本结构示例

以下示例演示了 API1 的基本写法（示例中提到的API名字仅做示范）。

```
nicxapi 1;

tooltip IPCN = """  //集中设置鼠标提示，可以在最上面或者最下面写这部分，方便寻找
>纯公网: 1.DNSPod 2.DNSPod全球版 
>公网&教育: 40.中科大 41.南大 
>纯教育网: 60.武汉 61.北京 62.上海 63.广州 64.成都
""";

tooltip IPGFW = """
1.TestIPv6(US) 2.TestIPv6(CA) 3.TestIPv6(IN) 4.ipify
""";

tooltip IPCNYX = """
>公网    1.DNSPod 2.DNSPod全球版 
>公网/教育    60.科大
""";

//实际接口不一定要从4开始写，根据情况自己调整

provider IPCN 40 //定义一个接口，添加到`国内方向IP回显`使用，ID排序为4
{
    ipv4 "中科大4"  //定义IPv4回显接口。`中科大`（下同）是便于阅读和报错定位的名称，主程序不使用，下拉框只显示ID `4`
    {
        request response  //request为发起http请求，响应结果存放到局部变量`response`。具体函数后面有介绍
        {
            url = "https://test.ustc.edu.cn";
            forceIPv4 = true;  //forceIPv4/forceIPv6参数只代表让查询器X做出尝试，不保证100%生效
        }
        return ValidateIP(Trim(response));  //这里对返回值`response`先删首尾空后，确保返回值为有效IP再返回
    }

    ipv6 "中科大6"  //定义IPv6回显接口。删除此部分则不使用IPv6（上同）。删除此部分并将IPv4接口url定义为双栈域名，则建议添加到`IPCNYX`作为双栈测试接口使用
    {
        request response
        {
            url = "https://test6.ustc.edu.cn";
            forceIPv6 = true;
        }
        return ValidateIP(Trim(response));
    }
}

//更多接口略

provider IPGFW 4 //定义一个接口，添加到`国外方向IP回显`使用，ID排序为4
{
	//此处写法略（同上）
}

//更多接口略

provider IPCNYX 60 //定义一个接口，添加到`双栈优先测试接口`使用，ID排序为60
{
    ipv4 "科大"  //定义IPv4接口，但实际作为双栈测试使用，该接口需支持双栈且支持直接回显IP（最终访问使用的IP）
    {
        request response  
        {
            url = "https://api64.ipify.org";
        }
        return ValidateIP(Trim(response));  //优先测试时，请确保返回值为纯IP再返回，否则软件会判断错误！
    }
}

//更多接口略
```

### 4.2 API1 分组

API1 Provider 只能使用以下分组，分别对应程序主界面的：从国内查（接口）、从国内查（优先接口）、从国外查（接口）：

```
provider IPCN 6 { ... }
provider IPCNYX 6 { ... }
provider IPGFW 6 { ... }
```

### 4.3 API1 返回值

每个 `ipv4` 或 `ipv6` 方法最终必须有一个 `return`：

```
return value;
```

`return`需要返回一个字符串，理论上可以是任何文本。返回的文本将直接显示在对应处，并运用到后续查询步骤（调用 API2 查询其地理位置）。因此，建议确保返回值为纯 IPv4/IPv6 地址。你也可以根据自己喜好调整返回值。使用非 IP 地址类型的返回值时，可能影响后续 API2 查询其地理位置。

如无法确保返回值纯净，可使用以下方法，自动尝试简单的正则提纯 IP 文本，不区分 IPv4/IPv6。提取不到时返回空。

```
return ValidateIP(response);
```

如网页混有其他文字，或想指定提取协议，也可使用（同样为简单正则）：

```
return ExtractIP(response, false); // 提取 IPv4
return ExtractIP(response, true);  // 提取 IPv6
```

该 3 个方法返回其提取到的第一个 IP 地址。

## 五、API2：IP 归属地查询接口

API2 采用“先定义 geo 方法，再由 Provider 引用”的结构。因此，同一个接口可同时引用到国内和国外列表使用，无需复制两遍。

API2 方法默认提供以下变量，由程序自动传入：

- `ip`：需要查询的目标 IP。

### 5.1 基本结构示例

以下示例演示了 API2 的基本写法（示例中提到的API名字仅做示范）：

```
nicxapi 1;

tooltip GEOCN = """
1.程序内置本地库  2.CZ88
""";

tooltip GEOGFW = """
1.程序内置IP2Region  2.CZ88
""";

provider GEOCN 2  //国内对应ID引用的接口。支持分行书写，也支持如下面的一行书写（空格分割）
{
    use CZ88;
}

provider GEOGFW 2 { use CZ88; }  //国外对应ID接口。如果国内外均引用同一个接口，直接复制use即可

geo CZ88  //定义接口的名字，可任取，use时区分大小写
{
    request json    //request为发起http请求，响应结果存放到局部变量`json`。具体函数后面有介绍
    {
        url = Concat("https://cz88.net/", ip);  //配置文件不支持+、$"{}"等语法，使用Concat函数拼接
    }

    returnGeoIf Or(IsEmpty(json), HasPrefix(json, "error")), "", "";  //如果Json以error开头则返回

    let country = GetJson(json, "country");  //定义局部变量并赋值
    let region = GetJson(json, "region");
    let city = GetJson(json, "city");
    let isp = GetJson(json, "connection.isp");
    let asn = GetJson(json, "connection.asn");
    let org = GetJson(json, "connection.org");

    returnGeo  返回时用英文逗号分割两段文本（文本1、文本2，建议分别对应GEO、AS）
        Concat(country, "/", region, "/", city, "/", isp),
        Concat("AS", asn, " ", org);
}
```

### 5.2 API2 分组

API2 Provider 只能使用：

```
provider GEOCN 10  { use ...; }
provider GEOGFW 10 { use ...; }
```

每个 `use` 指向一个已定义的 `geo` 方法。方法名必须唯一。Provider 可写在 `geo` 定义之前或之后。

### 5.3 API2 返回值

API2 必须用 `returnGeo` 返回两个字符串：

```
returnGeo text1, text2;
```

`returnGeo`需要返回两个字符串，理论上可以是任何文本。返回的文本将直接显示在对应处。因此，建议`text1`返回地理位置等信息、`text2`返回AS运营商等信息。你也可以根据自己喜好调整返回值，不必强制遵循示例格式。例：

```
returnGeo "中国/湖北/武汉/洪山/移动", "AS9929 China Mobile Hubei Province Network";
```

将在程序调用地理位置处显示上述信息（主界面 IP 地址下面的两行文本、手动查询IP窗口的两个文本框是分别对应 `text1` `text2` ；Trace+自定义地理位置时，`text1` `text2` 会直接显示为一行）。

失败时通常返回两个空字符串（也可选择返回其他文本）：

```
returnGeoIf Or(IsEmpty(json), HasPrefix(json, "error")), "", "";
```

## 六、可用语句

### 6.1 `request`

执行一次 HTTP 请求，并把结果保存到变量：

```
request json
{
    url = "https://example.com/api";
}
```

后续可直接使用 `json`。

### 6.2 `requestIf`

只有条件为 `true` 时才发送请求：

```
requestIf IsEmpty(json), json
{
    url = "https://example.com/fallback";
}
```

它可覆盖同名变量，适合备用接口、IPv4/IPv6 分支等场景。此例意味若`json`为空则访问新的url，并将访问结果替换进`json`。

### 6.3 `let`

定义局部变量。

```
let country = GetJson(json, "country");
let result = Concat(country, "/", city);
```

允许重新给同名变量赋值。

### 6.4 `return` 和 `returnIf`

用于 API1：

```
return test;
returnIf IsEmpty(response), test;
```

### 6.5 `returnGeo` 和 `returnGeoIf`

用于 API2：

```text
returnGeo text1, text2;
returnGeoIf failed, text1, text2;
```

`return`需要返回一个字符串，`returnGeo`需要返回两个字符串。理论上所有返回的字符串可以是任何文本，但建议`text1`返回地理位置等信息、`text2`返回AS运营商等信息。你也可以根据自己喜好调整返回值。

每个 API1 方法必须至少有一个最终 `return`；每个 API2 方法必须至少有一个 `returnGeo`。

方法执行到返回语句后立即结束。

### 6.6 特例：`return` 四行返回值

若你的 API 返回的字符串中，同时包含 IP 和该 IP 的地理位置，且你希望直接使用该信息作为最终查询结果，不需要再用 API2 里的 API 发起地理位置查询，则可以使用“四行返回值”特例，直接在 API1 中就 `return`并最终显示，不再发起 API2 查询。该特例仅适用于 API1 `return`。

排版规则为：按行分割，第 1 行作为 API1 返回的最终 IP 地址，第 2 行自动替代 API2 的返回值1，第 3~4 行自动合并后替代 API2 的返回值2。建议换行符格式为`Windows (CR+LF)`。

例如，某 API 返回值 `response` 如下：

```
183.95.135.135
中国 湖北省武汉市中国联通
AS4837
ChinaUnicom Hubei province network
```

该返回值为查询器X认定为标准的“四行返回值”。此时你只需要在 API1 中：

```
return response
```

查询器X会自动将上述`response`信息按规则整合并显示到主窗口中：

```
183.95.135.135
中国 湖北省武汉市中国联通
AS4837 ChinaUnicom Hubei province network
```

这些信息将一次显示出来，不再发起 API2 的地理位置查询。

提示：如有其他 API 返回值同样包括 IP + 地理信息，但格式并非上述示例。此时，你可以利用文本处理函数（后面有详细介绍），手动提取并构造一个四行返回值后，程序会自动识别排版，效果与示例一样。

## 七、HTTP 请求

`request` 和 `requestIf` 支持以下属性：

| 属性 | 默认值 | 说明 |
| --- | --- | --- |
| `url` | 无 | 必填，请求地址，可是表达式 |
| `method` | `GET` | 支持 `GET`、`POST` |
| `postData` | 空 | POST 正文 |
| `useCurlUA` | `false` | 将 User-Agent 修改为 Curl 访问 |
| `useRandomUA` | `true` | 将 User-Agent 设置为随机值（自动随机生成符合规范的UA） |
| `encoding` | 自动 | 指定响应编码，如 `"GB2312"`、`"UTF-8"` |
| `forceIPv4` | `false` | 尝试使用 IPv4 发起请求* |
| `forceIPv6` | `false` | 尝试使用 IPv6 发起请求* |
| `responseHeader` | 空 | 从响应头中取出对应名称的参数，设置后返回该参数，而不是响应正文 |
| `timeoutMs` | `0` | 指定超时时间（毫秒）。`0` 表示不额外设置（使用软件默认设置10秒） |
| `cookieWarmup` | 空 | 非空时启用 Cookie 会话，并先访问该地址 |
| `cookieHost` | 请求 URL | 添加或读取 Cookie 时使用的站点根地址 |
| `ensureCookieName` | 空 | 若该 Cookie 不存在，自动生成 UUID 值 |
| `attempts` | `1` | Cookie 会话模式下的最大尝试次数，最小为 1 |
| `successJsonPath` | 空 | Cookie 模式下指定成功字段；其值为 `true` 时结束重试 |

### 7.1 POST 和 Header 示例

下面示例演示了如何使用 POST 方法、携带 Header 访问目标 API 

```
request json
{
    url = "https://example.com/api/query";
    method = POST;
    postData = Concat("ip=", EncodeUrl(ip));
    useRandomUA = false;
    encoding = "UTF-8";

    header "Accept" = "application/json";
    header "Content-Type" = "application/x-www-form-urlencoded";
    header "Authorization" = "Token";
}
```

Header 名称必须放在双引号内。Header 的值可是字符串、变量或函数表达式。

### 7.2 从响应头读取信息

```
request value
{
    url = "https://example.com/redirect";
    responseHeader = "Location";
}
return value;
```

适用于部分 API 将需要的信息放置在响应头而非正文的情况使用。

## 八、可用函数

查询器X所有的配置文件（包括但不限于API1/2）均可使用且只能使用下列函数。

下列函数中，未括号标注类型的参数/返回值，其类型均为字符串（文本型）

### 8.1 IP 文本处理

| 函数 | 说明 |
| --- | --- |
| `ExtractIP(text, [bool] isIPv6)` | 从混合文本中尝试提取 IPv4 或 IPv6，返回找到的第一个值。 |
| `ValidateIP(text)` | 验证并整理文本是否为 IP 地址，支持 IPv4 或 IPv6，失败返回空 |
| `[bool] IsIPv4(text)` | 判断是否为有效 IPv4 |
| `[bool] IsIPv6(text)` | 判断是否为有效 IPv6 |
| `MaskLastIPv4(ip)` | 需传入 IPv4 ，自动最后一段替换成 `0`；非 IPv4 返回空值 |

### 8.2 JSON 文本处理

| 函数 | 说明 |
| --- | --- |
| `GetJson(json, path)` | 按路径读取 JSON 值（主用） |
| `GetKeysJson(json, key1, key2, ...)` | 将每一级键分别作为参数，按参数顺序逐层读取 JSON 值 |
| `CleanJson(json)` | 去除 UTF-8 BOM 并清理首尾空白 |
| `CheckJson(json, text, path1, path2, ...)` | 任一路径为 `true` 或 `1` 时返回指定的 `text`，否则返回空字符串 |

JSON 路径支持对象和数组，例：

```
GetJson(json, "country.name")
GetJson(json, "subdivisions[0].names.zh-CN")
```

`GetKeysJson` 与点号路径一样按层级逐步读取，只是把原来由点号分隔的每一级路径改为独立参数：

```
GetJson(json, "data.location.city")
GetKeysJson(json, "data", "location", "city")
```

两者在上述示例中作用相同。`GetKeysJson` 的每个键都按完整文本处理，因此更适合键名本身包含点号的 JSON：

```
GetKeysJson(json, "data.info", "city")
```

`GetKeysJson` 目前不解析 `items[0]` 这类数组下标。需要读取数组时，请使用 `GetJson` 的点号路径和 `[序号]` 写法。

`CheckJson` 的第二个参数是命中条件后需要返回的自定义文字：

```
let risk = CheckJson(json, "<!!!>", "is_vpn", "is_proxy", "is_tor");
let mobile = CheckJson(json, "[Mobile]", "network.is_mobile");
```

### 8.3 通用文本处理

| 函数 | 说明 |
| --- | --- |
| `GetMidText(text, left, right)` | 相当于易语言“文本_取中间”（例：原文=`12345`,`GetMidText(原文, "2", "4")`=`3`） |
| `GetLeftText(text, marker)` | 相当于易语言“文本_取左边”，按字符定位（例：原文=`12345`,`GetLeftText(原文, "2")`=`1`） |
| `GetRightText(text, marker)` | 相当于易语言“文本_取右边”，按字符定位（例：原文=`12345`,`GetRightText(原文, "3")`=`45`） |
| `DelLeftText(text, [num] length)` | 相当于易语言“文本_删左边”，按长度删（例：原文=`12345`,`DeleteLeftText(原文, "2")`=`345`） |
| `DelRightText(text, [num] length)` | 相当于易语言“文本_删右边”，按长度删（例：原文=`12345`,`DeleteRightText(原文, "3")`=`12`） |
| `ReplaceText(text, old1, new1, old2, new2, ...)` | 替换文本。可单个使用，和 C# .Replace() 一样，也可连续使用（可将所有欲替换和替换后的文本一次性输入，避免反复嵌套）。 |
| `DecodeUnicode(text)` | 解码 `\uXXXX` 等 Unicode 转义 |
| `Trim(text)` | 删除首尾空白字符 |
| `TrimAll(text)` | 删除所有空白字符，包括空格、换行和制表符 |
| `Concat(value1, value2, ...)` | 将任意数量值拼接成字符串 |
| `MatchRegex(text, pattern)` | 返回正则表达式的第一个完整匹配结果 |
| `GetLineValue(text, key)` | 从 `Key: Value` 格式文本中读取 Value |
| `GetIni(text, key)` | 从 INI 文本的任意节中读取第一个同名键 |
| `GetIni(text, section, key)` | 从 INI 文本的指定节中读取键值 |
| `EncodeUrl(text)` | URL 参数编码 |

`ReplaceText` 替换文本按照从左到右的顺序连续替换。如：

```
let clean = ReplaceText(text, "[", "", "]", "", " ", "-");
```

这一条函数将会先删除 `[`，再删除 `]`，最后把空格替换为 `-`。不需要嵌套多个 `ReplaceText`。

`MatchRegex` 返回整个匹配内容，不返回捕获组。如需要捕获组，建议改用 `GetMidText`，或设计一个能直接匹配最终结果的正则表达式。

INI 接口返回内容可直接读取，节名和键名不区分大小写，空行以及以 `;`、`#` 开头的注释行会被忽略，键和值使用第一个 `=` 分隔；值中后续出现的 `=` 会原样保留。

```
// 假设 response 内容为：
// [network]
// ip=203.0.113.10
// isp=Example Network

let ipValue = GetIni(response, "network", "ip");
let ispValue = GetIni(response, "network", "isp");

// 不确定键位于哪个节时，也可省略 section：
let firstIp = GetIni(response, "ip");
```

### 8.4 条件与判断

| 函数 | 说明 |
| --- | --- |
| `If([bool] condition, trueValue, falseValue)` | 条件选择 |
| `[bool] Equals(a, b)` | 不区分大小写比较文本 |
| `[bool] IsEmpty(value)` | 判断是否为 null、空字符串或只含空白 |
| `[bool] Contains(text, value)` | 判断是否包含指定文本，区分大小写 |
| `[bool] HasPrefix(text, value)` | 判断是否以指定文本开头，区分大小写 |
| `[bool] And(a, b)` | 两个条件同时为 true |
| `[bool] Or(a, b)` | 任一条件为 true |
| `[bool] Not(value)` | 条件取反 |

复杂条件可嵌套：

```
let failed = Or(
    IsEmpty(json),
    HasPrefix(json, "error")
);
```

`If`、`And`、`Or` 是表达式函数，不用于控制 HTTP 请求是否发送。需要避免一次请求时，请使用 `requestIf`。

### 8.5 随机选择和短期缓存

| 函数 | 说明 |
| --- | --- |
| `ChooseRandom(a, b, ...)` | 随机返回一个参数；没有参数时返回空值 |
| `GetCache(name)` | 读取程序本次运行期间的短期缓存 |
| `SetCache(name, value, minutes)` | value 非空时写入缓存，并返回 value |
| `SelectMaxJson(json, name1, path1, name2, path2, ...)` | 比较多个 JSON 路径下的数值，随机返回最高值对应的名称。当API支持状态查询时使用 |

缓存只存在于当前程序进程，重启后清空，不写入磁盘。

`SelectMaxJson` 必须传入 JSON，后面再传入一组或多组“名称、JSON路径”：

```
let node = SelectMaxJson(
    statusJson,
    "nodeA", "nodes.a.uptime",
    "nodeB", "nodes.b.uptime"
);
```

如无法读取任何有效数字，返回空字符串。

### 8.6 调试输出

如需临时查看、输出内部部分文本字符串，可使用下列调试输出函数

| 函数 | 说明 |
| --- | --- |
| `MessageBox(text)` | 调试输出（使用普通信息框显示 `text`） |
| `Debug(text, name, place)` | 调试输出（将`text`写入指定位置文本文档） |

`Debug` 示例：

```
Debug(response, "ip138", "E:\\NetInfoCheckerX\\bin\\Release");
```

上述示例将 `response` 保存到该目录下的 `ip138.txt`。文件后缀固定为 `.txt`无需设置。

`name` 或 `place` 任意值为空、目标目录不存在或指定位置保存失败时，程序会尝试保存到主程序旁边，默认文件名格式为：`NICX_Temp_yymmdd_HHmmss.txt`

## 九、故障切换 (Fallback) 结构示例

如需要在同一个 Provider 中提供 Fallback（即第一个 API 地址访问失败等情况下，自动切换访问第二个甚至第三个 API 地址），可参考示例写法。示例写法示范为 API2，API1 可使用同样方法书写，相关函数替换即可。

```
geo GeoWithFallback
{
    request json
    {
        url = Concat("https://example.com/source-a?ip=", ip);
    }

    requestIf IsEmpty(GetJson(json, "ip")), json
    {
        url = Concat("https://example.com/source-b?ip=", ip);
    }

    returnGeoIf IsEmpty(GetJson(json, "ip")), "", "";

    returnGeo
        GetJson(json, "location"),
        GetJson(json, "asn");
}
```

## 十、常见问题和注意事项

### 10.1 修改配置文件后程序内没有变化？

目前不支持热重载。请退出查询器X后重新启动，或确认文件位于实际运行 exe 的旁边。临时启动时需注意。

### 10.2 写好的所有外置接口都消失了？

配置采用整份验证。任意一处错误，都可能使整份文件加载失败。请查看程序旁边对应的 `.error.log`。

### 10.3 可以直接写 C# 吗？

不可以。查询器X的 API 配置文件不是 C# 语法，也不是原生的 C# 编译器，只支持本文列出的语法。其他未列出方法均不可以使用。

### 10.4 变量拼错会怎样？

部分没有定义的标识符会按普通文本处理，这是为了允许 `GET`、`POST` 这类简写。因此变量名拼错不一定在加载时立即报错。建议变量名保持简单，避免与常用缩写冲突，并检查每个 `request` 的结果变量是否与后面使用一致。

### 10.5 URL 中如何加入目标 IP 或 Key /  Token等信息？

```
let key = "替换成自己的Key";
let url = Concat("https://example.com/query?ip=", ip, "&key=", key);
```

如参数可能包含特殊字符：

```
let postData = Concat("ip=", EncodeUrl(ip));
```

### 10.6 API1 与 API2 的区别？

| 项目 | API1 | API2 |
| --- | --- | --- |
| 用途 | （主界面）回显本机公网 IP | （主界面、Trace+、手动查询IP）显示指定 IP 的位置等信息 |
| 目标 IP 变量 | 不需要 | 固定且自动传入 `ip` |
| 定义方式 | Provider ，内部 `ipv4`/`ipv6` | 独立 `geo`，由 Provider 使用 `use` 引用已有 `geo` |
| 返回语句 | `return` | `returnGeo` |
| 返回内容 | 一个字符串（通常为 IP 地址文本） | 两个字符串，可根据实际情况选择返回内容 |

### 10.7 会上传、分享我的 API 配置吗?

不会。API配置完全从本机程序目录读取，除非你主动分享，否则其他人永远不知道你的配置文件。

程序也不会上传获取你的配置给开发者，我没兴趣，拿着也没啥用。常见常用的 API 我曾经都内置过，作为一名业余开发者也得有最基本的道德，所以不会为了开阔视野或者怎么样的任何目的而获取你们的 API 配置，真获取了也会被你们发现，没意义。
