namespace SssjServer.Game;

/// <summary>
/// 货币 id 常量。取证：<c>extracted/lua/MoneyLogic.lua:10..45</c>（客户端与服务端共用同一套 id），
/// 与 <c>extracted/setting/MoneyDef.txt</c> 的「代币ID」列一致：
/// <code>
/// 1  ui.Type_TiLi         体力
/// 2  ui.Type_Gold         金币(免费)
/// 3  ui.Type_Free_Diamond 钻石(地球币、免费货币)
/// 4  ui.Type_DatePoint    约会点数
/// 12 eMoneyType_Pay_Diamond 钻石（付费）
/// </code>
/// 只收录服务端目前会主动读写的几种，其余按需再加（数值必须与 MoneyLogic.lua 保持一致）。
/// </summary>
public static class MoneyType
{
    /// <summary>体力（MoneyLogic.eMoneyType_Vigour = 1）。</summary>
    public const int Vigour = 1;

    /// <summary>免费金币（MoneyLogic.eMoneyType_Gold = 2）。</summary>
    public const int Gold = 2;

    /// <summary>免费钻石（MoneyLogic.eMoneyType_Free_Diamond = 3）。</summary>
    public const int FreeDiamond = 3;

    /// <summary>约会点数（MoneyLogic.eMoneyType_DatePoint = 4）。</summary>
    public const int DatePoint = 4;

    /// <summary>咖啡币（MoneyLogic.eMoneyType_CoffeeCoin = 7）。</summary>
    public const int CoffeeCoin = 7;

    /// <summary>付费钻石（MoneyLogic.eMoneyType_Pay_Diamond = 12）。</summary>
    public const int PayDiamond = 12;

    /// <summary>Tower lottery tokens (MoneyLogic.eMoneyType_Lottery = 14).</summary>
    public const int Lottery = 14;

    /// <summary>
    /// 造型U型夹（<c>MoneyLogic.eMoneyType_Hairpin = 62</c>，<c>MoneyLogic.lua:57</c>）。
    /// ★ 十面妆造（妆扮商店，<c>LabelIdx = 101</c>）在售商品的主要货币。
    /// </summary>
    public const int Hairpin = 62;

    /// <summary>U型夹（<c>MoneyLogic.eMoneyType_FreeHairpin = 63</c>，<c>MoneyLogic.lua:58</c>）。</summary>
    public const int FreeHairpin = 63;

    /// <summary>货币 id → 中文名（仅日志用；未收录的返回 "?"）。</summary>
    public static string NameOf(int id) => id switch
    {
        Vigour => "体力",
        Gold => "金币",
        FreeDiamond => "免费钻",
        DatePoint => "约会点数",
        CoffeeCoin => "咖啡币",
        PayDiamond => "付费钻",
        Hairpin => "造型U型夹",
        FreeHairpin => "U型夹",
        _ => "?",
    };
}
