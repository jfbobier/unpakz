namespace Unpakz.Core.Zip;

/// <summary>IBM code page 437, the legacy ZIP name encoding when the UTF-8 flag is not set.</summary>
internal static class Cp437
{
    internal const string HighHalf =
        "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    internal static char Decode(byte b) => b < 0x80 ? (char)b : HighHalf[b - 0x80];
}
