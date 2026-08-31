网络综合查询器X

# 多出口测试 API 编写指南

查询器X的“多出口测试”已同主程序一起升级配置文件。支持从外置配置文件读取自定义的测试接口。你可自行增删、替换或调整接口，无需修改主程序。

提示：多出口测试API独立后，“多出口测试”各个窗口的名字更多只是对该窗口规模的介绍，并不设置“教育网窗口只能使用教育网API”等任何要求。你可以将任何API放置在任何窗口中，只要遵循了查询器X可用的语法，就可以使用。

本文只介绍多出口测试配置独有的写法。文件版本、注释、字符串、表达式、HTTP 请求、Header、文本提取、JSON/INI 读取及其他通用函数，参见《[IP回显/定位API编写指南]()》，本文不再重复。

截至11.2609.1.0版本，多出口测试使用以下配置文件：

- `NICX_MultiLite.nicxapi`：多出口精简版。
- `NICX_MultiFull.nicxapi`：多出口全能版。
- `NICX_MultiEdu.nicxapi`：多出口教育网。

文件使用 UTF-8 编码，并放在 `NetInfoCheckerX.exe` 旁边。

## 零、AI 编写建议

考虑到大家可能更愿意用 AI 编写配置文件，可[点击这里]()下载本 Wiki 完整文档。下载后，把`本文`、[IP回显/定位API编写指南(点击下载)]() 和 `目标 API 的官方文档` 一起交给 AI，同时提供你的想法让 AI 编写。

如 API 官方没有文档，则向 AI 提供你 API 的使用方法，包括但不限于：

- 目标 API 的 URL、访问方式、响应示范、IPv4/IPv6 支持情况及是否需要 Header、POST、Key 或 Token。
- 希望放入精简版、教育网还是全能版，并说明目标视觉分组及槽位 ID（不加载配置文件时，窗口显示的名称就是目标组件名称）。
- 是否需要 Fallback，以及切换后希望显示的备用接口标题。
- 等等

不要向不可信的 AI 提供真实私人 Key。可先用占位文字生成配置，再在本机替换。

## 一、加载时机与错误处理

多出口配置不会在查询器X主程序启动时加载，而是在对应窗口打开时加载一次。

修改配置后，请关闭并重新打开对应窗口，无需退出主程序，但已打开的窗口不会热重载。

### 1.1 文件不存在或有语法错误

多出口测试没有为所有槽位内置备用 API。因此，当配置文件不存在或加载失败时，不会回退到内置接口，具体行为如下：

- 多出口教育网：点击“开测”后提示配置错误并停止。
- 多出口精简版/全能版：外置 API 部分不会测试；MTU 测试（精简版）、UDP 测试和 HTTP 访问测速（全能版）不属于外置 API，仍可继续使用。

多出口配置和 IP 回显配置一致，同为整份验证：语法有错误即放弃加载整个文件，并输出错误日志。详细错误会写入主程序旁：

- `NICX_MultiLite.error.log`
- `NICX_MultiFull.error.log`
- `NICX_MultiEdu.error.log`

### 1.2 配置内节点数量与窗口槽位数量不同

查询器X会根据实际存在的槽位数量动态统计可用槽位。三个窗口均按视觉分组分别统计，一个分组增加或减少槽位不会影响其他分组。

- 配置少于窗口槽位：只加载已编写的 Provider；其余槽位保持默认名称，不自动复制、补齐或覆盖，不弹出警告。
- 配置多于窗口槽位：只加载窗口中实际存在的槽位数量，超出部分忽略；窗口加载后提示一次，并在对应 `.error.log` 中记录警告。被忽略的部分不会阻止其他正常槽位测试。

Provider 的“分组 + ID”须对应到实际槽位。相同 ID 可以在不同分组中分别使用；使用窗口中不存在的分组或编号可能导致错误并放弃加载整份文件。

提示：某个分组的配置数量没有超过该分组窗口槽位数时，如使用不存在的 ID，会按配置错误处理。因此建议参考程序窗口内已有的槽位名称和数量修改，不要凭空猜测编号。某分组配置数量超过该分组槽位数时，找不到槽位的超出项会被忽略，并弹出一次提示、写入日志，不会阻止其他有效槽位测试。

## 二、Provider (接口)

多出口配置仍使用 IP回显/定位API（后简称API1/API2）的编写逻辑：

```text
nicxapi 1;
```

每个 Provider 对应窗口中的一组“标题 + 测试结果”。与 API1 不同的是，这里会将引号内的 Provider 名称会直接显示在窗口标题位置。

每个多出口 Provider 必须且只能定义一个 `ipv4` 或一个 `ipv6`，但这里的 `ipv4` `ipv6` 表示该槽位预期返回的协议类型，方便修改测试使用，并不是强制该槽位强制返回该类型IP。实际返回值仍然由 API 自身决定。例如：

```text
provider MULTIEDUPAIR 1
{
    ipv4 "中科大4"
    {
		...
        return response;
    }
}
```

此 Provider 将在多出口教育网窗口的“成对双栈”分组槽位 01 中显示：标题替换为 `中科大4`，标题右边显示 `response` 的值。

以下写法均不允许：

- Provider 中同时包含 `ipv4` 和 `ipv6`，或者既没有 `ipv4` 也没有 `ipv6`。
- 使用 API2 的 `use` 或 `geo` 结构。
- 同一分组内重复使用相同 ID。
- 使用小于 1 的 ID。

### 2.1 返回内容

每个方法最终必须 `return` 返回一个字符串：

```text
return result;
```

返回内容会直接显示在对应结果位置，理论上可以是任意文本。但建议确保返回值为以下之一以方便你的使用：

- 纯 IPv4 或 IPv6 地址；
- `IP + 信息 (如地理位置+运营商等)`；

如果目标 API 的响应还包含 HTML、JSON 或其他文字，可以使用《IP回显/定位API编写指南》中介绍的函数手动构造最终返回值，以提取所需内容。

## 三、多出口精简版和教育网

精简版和教育网均按视觉板块划分 Provider 分组。你可临时移走配置文件后打开窗口，界面显示的名称就是对应控件名称。

### 3.1 文件和分组

精简版使用 `NICX_MultiLite.nicxapi` ，教育网使用 `NICX_MultiEdu.nicxapi`。

教育网分组如下：

| 分组 | 视觉区域 | 编号顺序 | 标题/结果控件 | 当前槽位 |
| --- | --- | --- | --- | --- |
| `MULTIEDUPAIR` | 上方：IPv4、IPv6 使用两个链接成对测试 | 从上到下，每行先左后右 | `labelPair01` / `lblPair01` | 01～16 |
| `MULTIEDUV4` | 下方左侧：单链接仅显示 IPv4 | 从上到下 | `labelV401` / `lblV401` | 01～06 |
| `MULTIEDUDUAL` | 下方右侧：单链接可同时显示 IPv4/IPv6 | 从上到下 | `labelDual01` / `lblDual01` | 01～04 |

精简版分组如下：

| 分组 | 视觉区域 | 编号顺序 | 标题/结果控件 | 当前槽位 |
| --- | --- | --- | --- | --- |
| `MULTILITEIP` | 顶部 IP + 位置 | 从上到下 | `labelIP01` / `lblIP01` | 01～05 |
| `MULTILITEISP` | 中部多运营商 | 左列从上到下为 01～04，右列为 05～08 | `labelISP01` / `lblISP01` | 01～08 |
| `MULTILITEMISC` | 下部杂类 | 以窗口显示的控件名称为准 | `labelMisc14` / `lblMisc14` 等 | 14～21、23～24 |

各分组独立统计槽位、独立使用 ID。因此可以只增减某一个视觉板块，而不需要调整其他板块的 Provider。不同分组可以重复使用相同 ID。

例如：

```text
provider MULTIEDUPAIR 1
{
    ipv4 "中科大4"
    {
        request r
        {
            url = "http://test.ustc.edu.cn";
        }
        return r;
    }
}
```

Provider 的分组和 ID 共同对应控件。例如：

```text
provider MULTIEDUPAIR 3  // labelPair03 + lblPair03
provider MULTIEDUV4 3    // labelV403 + lblV403
provider MULTILITEIP 2   // labelIP02 + lblIP02
provider MULTILITEISP 6  // labelISP06 + lblISP06
```

Provider ID 写普通整数即可，不必补零；控件名称为了对齐使用两位数字。引号内的名称会替换对应 `label...` 标题，接口返回值显示在对应 `lbl...`。

窗口内的可用槽位数量等并非永久固定，后续版本可能会调整增删，请以实际窗口为准。

建议在开始编写之前，先不加载配置文件，直接打开对应窗口，查看界面上槽位数量，再开始编写。

### 3.2 鼠标提示

鼠标提示按单个槽位编写，格式为：

```text
tooltip MULTIEDUPAIR07 = "点击输入文字";
```

提示名称由“完整分组名 + 槽位编号”组成。建议编号补足两位，与控件名称保持一致。设置提示后，对应标题会显示下划线；鼠标移到标题上即可查看提示。

不需要提示的槽位可以不写 `tooltip`。

### 3.3 基本示例

下面示范教育网“成对双栈”分组的前两个槽位。精简版使用相同语法，但须换成上表中的精简版分组名。具体可用函数参见《IP回显/定位API编写指南》。

```text
nicxapi 1;

tooltip MULTIEDUPAIR01 = "示范接口：返回当前出口的 IPv4 地址。";

provider MULTIEDUPAIR 1
{
    ipv4 "中科大4"
    {
        request response
        {
            url = "http://test.ustc.edu.cn";
            forceIPv4 = true;
        }

        return ExtractIP(response, false);
    }
}

provider MULTIEDUPAIR 2
{
    ipv6 "中科大6"
    {
        request response
        {
            url = "http://test6.ustc.edu.cn";
            forceIPv6 = true;
        }

        return ExtractIP(response, true);
    }
}
```

示例仅供参考。

## 四、多出口全能版

### 4.1 文件和分组

全能版使用 `NICX_MultiFull.nicxapi`。

全能版按照窗口视觉区域分为三个 Provider 分组：

| 分组 | 对应方向 | 标题控件 | 结果控件 | 当前槽位 |
| --- | --- | --- | --- | --- |
| `MULTIFULLCN` | 国内方向 | `labelCN01` | `lblCN01` | 01～32 |
| `MULTIFULLGFW` | 国外/代理方向 | `labelGFW01` | `lblGFW01` | 01～12 |
| `MULTIFULLDUAL` | 双栈、多运营商方向 | `labelDual01` | `lblDual01` | 01～41 |

Provider 的数字 ID 与控件末尾编号对应：

```text
provider MULTIFULLCN 1      // labelCN01 + lblCN01
provider MULTIFULLGFW 3     // labelGFW03 + lblGFW03
provider MULTIFULLDUAL 21   // labelDual21 + lblDual21
```

Provider ID 写作普通整数即可，不必写成 `01`。窗口控件和鼠标提示为便于对齐美观，使用两位数字。

三个分组分别统计槽位。同一个 ID 可以在不同分组中各使用一次，例如 `MULTIFULLCN 1` 与 `MULTIFULLGFW 1` 不冲突。

### 4.2 鼠标提示

全能版同样按单个槽位设置提示：

```text
tooltip MULTIFULLCN01 = "国内方向示范接口。";
tooltip MULTIFULLGFW03 = "该接口可能跟随系统代理出口。";
tooltip MULTIFULLDUAL21 = "IPv4 测试接口。";
```

建议全能版的提示编号使用两位数字，与控件名称保持一致。程序也兼容不补零的写法。

设置提示后，对应标题会显示下划线。配置中的 `tooltip` 只绑定标题 `label...`，不会覆盖结果 `lbl...` 的默认提示。

所有结果位置会根据当前显示内容自动生成鼠标提示，无需修改：

```text
当前结果文字
(双击复制, 右键刷新本接口)
```

### 4.3 附加测试部分

全能版窗口顶部的 STUN 测试和底部的 HTTP 访问测速、精简版窗口顶部的 MTU 测试，仍由程序自身实现，不使用 `NICX_MultiFull.nicxapi`。

不要在配置中为它们编写 Provider，它们也不占用配置文件的槽位。

### 4.4 基本示例

多出口全能版中的 Provider 分组，只决定它显示在哪一个窗口区域，实际访问该接口使用的出口和设置等，依然使用系统默认网卡，不做任何修改。测试结果以实际为准。

```text
nicxapi 1;

tooltip MULTIFULLCN01 = "国内方向 IPv4 回显示范。";
tooltip MULTIFULLGFW01 = "国外方向 IPv4 回显示范。";
tooltip MULTIFULLDUAL01 = "IPv6 回显示范。";

provider MULTIFULLCN 1
{
    ipv4 "国内示例"
    {
        request response
        {
            url = "https://test.ustc.edu.cn";
            forceIPv4 = true;
        }

        return ExtractIP(response, false);
    }
}

provider MULTIFULLGFW 1
{
    ipv4 "国外示例"
    {
        request response
        {
            url = "https://www.google.com/ip";
            forceIPv4 = true;
        }

        return ExtractIP(response, false);
    }
}

provider MULTIFULLDUAL 1
{
    ipv6 "双栈示例6"
    {
        request response
        {
            url = "https://api6.ipify.org";
            forceIPv6 = true;
        }

        return ExtractIP(response, true);
    }
}
```

示例仅供参考。

## 五、故障切换 (Fallback) 

为降低维护频次，允许一个槽位先访问主接口，主接口未返回有效结果时再访问另一个甚至多个备用接口。《IP回显/定位API编写指南》中提到的 Fallback 方法等在多出口测试中依然可用。

为了便于修改槽位标题，以明确实际使用的 API 接口，全能版和精简版支持两个用于修改当前槽位标题的语句：

```text
setTitle "备用接口名称";
setTitleIf condition, "备用接口名称";
```

| 语句 | 作用 |
| --- | --- |
| `setTitle text;` | 立即把当前槽位标题修改为指定文本 |
| `setTitleIf condition, text;` | 条件为 `true` 时修改标题，否则不修改 |

标题只影响界面显示，不会切换请求。真正的备用请求仍需自行编写（如 `requestIf`）。

每次重新测试该槽位时，标题会先恢复为 Provider 中 `ipv4`/`ipv6` 引号内的主接口名称；只有再次进入 Fallback 时才改成备用名称。

`setTitle` 和 `setTitleIf` 在全能版、精简版生效；教育网窗口暂不处理动态标题修改。

**完整 Fallback 示例**

```text
provider MULTIFULLCN 6
{
    ipv4 "主接口A"
    {
        request responseA
        {
            url = "https://api.ipify.org";
            forceIPv4 = true;
        }

        let ipA = ExtractIP(responseA, false);
        let useFallback = Not(IsIPv4(ipA));

        // 条件成立时，告诉用户当前结果来自备用接口B
        setTitleIf useFallback, "备用接口B";

        // requestIf 未执行时也可能在后面引用 responseB，故先初始化为空
        let responseB = "";

        requestIf useFallback, responseB
        {
            url = "https://api2.ipify.org";
            forceIPv4 = true;
        }

        let ipB = ExtractIP(responseB, false);
        return If(useFallback, ipB, ipA);
    }
}
```

如备用接口也失败，建议最终返回空字符串或明确的错误文字。还可继续增加第二个条件和第三个接口，但配置会变得更复杂，建议逐步调试。
