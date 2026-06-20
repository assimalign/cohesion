namespace Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

/// <summary>
/// The canonical HPACK Huffman code table from RFC 7541 Appendix B.
/// Indexed by symbol (0-255 octets, 256 = EOS). Each entry is the
/// (code, bit-length) pair the spec defines.
/// </summary>
/// <remarks>
/// <para>
/// Codes are emitted MSB-first on the wire. The assignment of symbols
/// to numerical codes inside a length group is specified explicitly by
/// the RFC and is not derivable from frequency or lexicographic order
/// alone.
/// </para>
/// <para>
/// Every entry here is cross-checked against the RFC 7541 Appendix C
/// example corpus — each canonical encoded value in the spec decodes to
/// exactly the expected string.
/// </para>
/// </remarks>
internal static class HPackHuffmanCodes
{
    /// <summary>
    /// EOS symbol index. Used as terminal padding for Huffman strings;
    /// MUST NOT appear standalone in a decoded sequence (RFC 7541 §5.2).
    /// </summary>
    public const int EndOfString = 256;

    /// <summary>
    /// The Huffman code for each symbol. <c>(Code, BitLength)</c> tuples;
    /// index 0..255 is the octet, index 256 is EOS.
    /// </summary>
    public static readonly (uint Code, byte BitLength)[] Table =
    {
        /*   0 */ (0x1ff8u, 13),       /*   1 */ (0x7fffd8u, 23),      /*   2 */ (0xfffffe2u, 28),      /*   3 */ (0xfffffe3u, 28),
        /*   4 */ (0xfffffe4u, 28),    /*   5 */ (0xfffffe5u, 28),     /*   6 */ (0xfffffe6u, 28),      /*   7 */ (0xfffffe7u, 28),
        /*   8 */ (0xfffffe8u, 28),    /*   9 */ (0xffffeau, 24),      /*  10 */ (0x3ffffffcu, 30),     /*  11 */ (0xfffffe9u, 28),
        /*  12 */ (0xfffffeau, 28),    /*  13 */ (0x3ffffffdu, 30),    /*  14 */ (0xfffffebu, 28),      /*  15 */ (0xfffffecu, 28),
        /*  16 */ (0xfffffedu, 28),    /*  17 */ (0xfffffeeu, 28),     /*  18 */ (0xfffffefu, 28),      /*  19 */ (0xffffff0u, 28),
        /*  20 */ (0xffffff1u, 28),    /*  21 */ (0xffffff2u, 28),     /*  22 */ (0x3ffffffeu, 30),     /*  23 */ (0xffffff3u, 28),
        /*  24 */ (0xffffff4u, 28),    /*  25 */ (0xffffff5u, 28),     /*  26 */ (0xffffff6u, 28),      /*  27 */ (0xffffff7u, 28),
        /*  28 */ (0xffffff8u, 28),    /*  29 */ (0xffffff9u, 28),     /*  30 */ (0xffffffau, 28),      /*  31 */ (0xffffffbu, 28),
        /*  32 */ (0x14u, 6),          /*  33 */ (0x3f8u, 10),         /*  34 */ (0x3f9u, 10),          /*  35 */ (0xffau, 12),
        /*  36 */ (0x1ff9u, 13),       /*  37 */ (0x15u, 6),           /*  38 */ (0xf8u, 8),            /*  39 */ (0x7fau, 11),
        /*  40 */ (0x3fau, 10),        /*  41 */ (0x3fbu, 10),         /*  42 */ (0xf9u, 8),            /*  43 */ (0x7fbu, 11),
        /*  44 */ (0xfau, 8),          /*  45 */ (0x16u, 6),           /*  46 */ (0x17u, 6),            /*  47 */ (0x18u, 6),
        /*  48 */ (0x0u, 5),           /*  49 */ (0x1u, 5),            /*  50 */ (0x2u, 5),             /*  51 */ (0x19u, 6),
        /*  52 */ (0x1au, 6),          /*  53 */ (0x1bu, 6),           /*  54 */ (0x1cu, 6),            /*  55 */ (0x1du, 6),
        /*  56 */ (0x1eu, 6),          /*  57 */ (0x1fu, 6),           /*  58 */ (0x5cu, 7),            /*  59 */ (0xfbu, 8),
        /*  60 */ (0x7ffcu, 15),       /*  61 */ (0x20u, 6),           /*  62 */ (0xffbu, 12),          /*  63 */ (0x3fcu, 10),
        /*  64 */ (0x1ffau, 13),       /*  65 */ (0x21u, 6),           /*  66 */ (0x5du, 7),            /*  67 */ (0x5eu, 7),
        /*  68 */ (0x5fu, 7),          /*  69 */ (0x60u, 7),           /*  70 */ (0x61u, 7),            /*  71 */ (0x62u, 7),
        /*  72 */ (0x63u, 7),          /*  73 */ (0x64u, 7),           /*  74 */ (0x65u, 7),            /*  75 */ (0x66u, 7),
        /*  76 */ (0x67u, 7),          /*  77 */ (0x68u, 7),           /*  78 */ (0x69u, 7),            /*  79 */ (0x6au, 7),
        /*  80 */ (0x6bu, 7),          /*  81 */ (0x6cu, 7),           /*  82 */ (0x6du, 7),            /*  83 */ (0x6eu, 7),
        /*  84 */ (0x6fu, 7),          /*  85 */ (0x70u, 7),           /*  86 */ (0x71u, 7),            /*  87 */ (0x72u, 7),
        /*  88 */ (0xfcu, 8),          /*  89 */ (0x73u, 7),           /*  90 */ (0xfdu, 8),            /*  91 */ (0x1ffbu, 13),
        /*  92 */ (0x7fff0u, 19),      /*  93 */ (0x1ffcu, 13),        /*  94 */ (0x3ffcu, 14),         /*  95 */ (0x22u, 6),
        /*  96 */ (0x7ffdu, 15),       /*  97 */ (0x3u, 5),            /*  98 */ (0x23u, 6),            /*  99 */ (0x4u, 5),
        /* 100 */ (0x24u, 6),          /* 101 */ (0x5u, 5),            /* 102 */ (0x25u, 6),            /* 103 */ (0x26u, 6),
        /* 104 */ (0x27u, 6),          /* 105 */ (0x6u, 5),            /* 106 */ (0x74u, 7),            /* 107 */ (0x75u, 7),
        /* 108 */ (0x28u, 6),          /* 109 */ (0x29u, 6),           /* 110 */ (0x2au, 6),            /* 111 */ (0x7u, 5),
        /* 112 */ (0x2bu, 6),          /* 113 */ (0x76u, 7),           /* 114 */ (0x2cu, 6),            /* 115 */ (0x8u, 5),
        /* 116 */ (0x9u, 5),           /* 117 */ (0x2du, 6),           /* 118 */ (0x77u, 7),            /* 119 */ (0x78u, 7),
        /* 120 */ (0x79u, 7),          /* 121 */ (0x7au, 7),           /* 122 */ (0x7bu, 7),            /* 123 */ (0x7ffeu, 15),
        /* 124 */ (0x7fcu, 11),        /* 125 */ (0x3ffdu, 14),        /* 126 */ (0x1ffdu, 13),         /* 127 */ (0xffffffcu, 28),
        /* 128 */ (0xfffe6u, 20),      /* 129 */ (0x3fffd2u, 22),      /* 130 */ (0xfffe7u, 20),        /* 131 */ (0xfffe8u, 20),
        /* 132 */ (0x3fffd3u, 22),     /* 133 */ (0x3fffd4u, 22),      /* 134 */ (0x3fffd5u, 22),       /* 135 */ (0x7fffd9u, 23),
        /* 136 */ (0x3fffd6u, 22),     /* 137 */ (0x7fffdau, 23),      /* 138 */ (0x7fffdbu, 23),       /* 139 */ (0x7fffdcu, 23),
        /* 140 */ (0x7fffddu, 23),     /* 141 */ (0x7fffdeu, 23),      /* 142 */ (0xffffebu, 24),       /* 143 */ (0x7fffdfu, 23),
        /* 144 */ (0xffffecu, 24),     /* 145 */ (0xffffedu, 24),      /* 146 */ (0x3fffd7u, 22),       /* 147 */ (0x7fffe0u, 23),
        /* 148 */ (0xffffeeu, 24),     /* 149 */ (0x7fffe1u, 23),      /* 150 */ (0x7fffe2u, 23),       /* 151 */ (0x7fffe3u, 23),
        /* 152 */ (0x7fffe4u, 23),     /* 153 */ (0x1fffdcu, 21),      /* 154 */ (0x3fffd8u, 22),       /* 155 */ (0x7fffe5u, 23),
        /* 156 */ (0x3fffd9u, 22),     /* 157 */ (0x7fffe6u, 23),      /* 158 */ (0x7fffe7u, 23),       /* 159 */ (0xffffefu, 24),
        /* 160 */ (0x3fffdau, 22),     /* 161 */ (0x1fffddu, 21),      /* 162 */ (0xfffe9u, 20),        /* 163 */ (0x3fffdbu, 22),
        /* 164 */ (0x3fffdcu, 22),     /* 165 */ (0x7fffe8u, 23),      /* 166 */ (0x7fffe9u, 23),       /* 167 */ (0x1fffdeu, 21),
        /* 168 */ (0x7fffeau, 23),     /* 169 */ (0x3fffddu, 22),      /* 170 */ (0x3fffdeu, 22),       /* 171 */ (0xfffff0u, 24),
        /* 172 */ (0x1fffdfu, 21),     /* 173 */ (0x3fffdfu, 22),      /* 174 */ (0x7fffebu, 23),       /* 175 */ (0x7fffecu, 23),
        /* 176 */ (0x1fffe0u, 21),     /* 177 */ (0x1fffe1u, 21),      /* 178 */ (0x3fffe0u, 22),       /* 179 */ (0x1fffe2u, 21),
        /* 180 */ (0x7fffedu, 23),     /* 181 */ (0x3fffe1u, 22),      /* 182 */ (0x7fffeeu, 23),       /* 183 */ (0x7fffefu, 23),
        /* 184 */ (0xfffeau, 20),      /* 185 */ (0x3fffe2u, 22),      /* 186 */ (0x3fffe3u, 22),       /* 187 */ (0x3fffe4u, 22),
        /* 188 */ (0x7ffff0u, 23),     /* 189 */ (0x3fffe5u, 22),      /* 190 */ (0x3fffe6u, 22),       /* 191 */ (0x7ffff1u, 23),
        /* 192 */ (0x3ffffe0u, 26),    /* 193 */ (0x3ffffe1u, 26),     /* 194 */ (0xfffebu, 20),        /* 195 */ (0x7fff1u, 19),
        /* 196 */ (0x3fffe7u, 22),     /* 197 */ (0x7ffff2u, 23),      /* 198 */ (0x3fffe8u, 22),       /* 199 */ (0x1ffffecu, 25),
        /* 200 */ (0x3ffffe2u, 26),    /* 201 */ (0x3ffffe3u, 26),     /* 202 */ (0x3ffffe4u, 26),      /* 203 */ (0x7ffffdeu, 27),
        /* 204 */ (0x7ffffdfu, 27),    /* 205 */ (0x3ffffe5u, 26),     /* 206 */ (0xfffff1u, 24),       /* 207 */ (0x1ffffedu, 25),
        /* 208 */ (0x7fff2u, 19),      /* 209 */ (0x1fffe3u, 21),      /* 210 */ (0x3ffffe6u, 26),      /* 211 */ (0x7ffffe0u, 27),
        /* 212 */ (0x7ffffe1u, 27),    /* 213 */ (0x3ffffe7u, 26),     /* 214 */ (0x7ffffe2u, 27),      /* 215 */ (0xfffff2u, 24),
        /* 216 */ (0x1fffe4u, 21),     /* 217 */ (0x1fffe5u, 21),      /* 218 */ (0x3ffffe8u, 26),      /* 219 */ (0x3ffffe9u, 26),
        /* 220 */ (0xffffffdu, 28),    /* 221 */ (0x7ffffe3u, 27),     /* 222 */ (0x7ffffe4u, 27),      /* 223 */ (0x7ffffe5u, 27),
        /* 224 */ (0xfffecu, 20),      /* 225 */ (0xfffff3u, 24),      /* 226 */ (0xfffedu, 20),        /* 227 */ (0x1fffe6u, 21),
        /* 228 */ (0x3fffe9u, 22),     /* 229 */ (0x1fffe7u, 21),      /* 230 */ (0x1fffe8u, 21),       /* 231 */ (0x7ffff3u, 23),
        /* 232 */ (0x3fffeau, 22),     /* 233 */ (0x3fffebu, 22),      /* 234 */ (0x1ffffeeu, 25),      /* 235 */ (0x1ffffefu, 25),
        /* 236 */ (0xfffff4u, 24),     /* 237 */ (0xfffff5u, 24),      /* 238 */ (0x3ffffeau, 26),      /* 239 */ (0x7ffff4u, 23),
        /* 240 */ (0x3ffffebu, 26),    /* 241 */ (0x7ffffe6u, 27),     /* 242 */ (0x3ffffecu, 26),      /* 243 */ (0x3ffffedu, 26),
        /* 244 */ (0x7ffffe7u, 27),    /* 245 */ (0x7ffffe8u, 27),     /* 246 */ (0x7ffffe9u, 27),      /* 247 */ (0x7ffffeau, 27),
        /* 248 */ (0x7ffffebu, 27),    /* 249 */ (0xffffffeu, 28),     /* 250 */ (0x7ffffecu, 27),      /* 251 */ (0x7ffffedu, 27),
        /* 252 */ (0x7ffffeeu, 27),    /* 253 */ (0x7ffffefu, 27),     /* 254 */ (0x7fffff0u, 27),      /* 255 */ (0x3ffffeeu, 26),
        /* 256 */ (0x3fffffffu, 30),
    };
}
