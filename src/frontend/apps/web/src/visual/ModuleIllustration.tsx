import type { ReactNode } from "react";

export type IllustrationKind = "dashboard" | "accounting" | "warehouse" | "sales" | "purchases" | "reports";

/**
 * Lightweight inline SVG module illustrations (flat shapes in the HASM palette, no raster images, no animation).
 * Drawn for the inline-end side of a header; mirrored in RTL by CSS so the soft edge always faces the title.
 * Decorative: never needed to understand information (aria-hidden).
 */
const ART: Record<IllustrationKind, ReactNode> = {
  dashboard: (
    <>
      <rect className="il-dark" x="150" y="26" width="160" height="96" rx="9" />
      <rect className="il-mint" x="159" y="35" width="142" height="78" rx="5" />
      <rect className="il-tur" x="176" y="84" width="14" height="20" rx="2" />
      <rect className="il-tur" x="198" y="72" width="14" height="32" rx="2" />
      <rect className="il-pri" x="220" y="60" width="14" height="44" rx="2" />
      <rect className="il-pri" x="242" y="48" width="14" height="56" rx="2" />
      <path className="il-stroke-gold" d="M172 80 L204 66 L236 56 L284 44" />
      <rect className="il-dark" x="132" y="122" width="196" height="8" rx="4" />
      <rect className="il-pri" x="338" y="66" width="62" height="62" rx="9" />
      <rect className="il-mint" x="346" y="74" width="46" height="13" rx="3" />
      <g className="il-tur">
        <rect x="346" y="94" width="12" height="9" rx="2" /><rect x="362" y="94" width="12" height="9" rx="2" /><rect x="378" y="94" width="14" height="9" rx="2" />
        <rect x="346" y="108" width="12" height="9" rx="2" /><rect x="362" y="108" width="12" height="9" rx="2" />
      </g>
      <rect className="il-gold" x="378" y="108" width="14" height="9" rx="2" />
      <path className="il-pri" d="M120 124 C108 100 112 82 128 70 C134 90 134 106 120 124Z" />
      <path className="il-tur" d="M120 124 C134 108 150 104 160 108 C148 124 134 128 120 124Z" />
    </>
  ),
  accounting: (
    <>
      <rect className="il-dark" x="146" y="22" width="118" height="106" rx="9" />
      <rect className="il-card" x="156" y="30" width="98" height="90" rx="5" />
      <rect className="il-gold" x="166" y="40" width="44" height="7" rx="3" />
      <g className="il-mint"><rect x="166" y="56" width="78" height="6" rx="3" /><rect x="166" y="70" width="78" height="6" rx="3" /><rect x="166" y="84" width="78" height="6" rx="3" /></g>
      <rect className="il-pri" x="166" y="100" width="54" height="9" rx="3" />
      <rect className="il-pri" x="290" y="58" width="66" height="70" rx="9" />
      <rect className="il-mint" x="298" y="66" width="50" height="14" rx="3" />
      <g className="il-tur"><circle cx="305" cy="94" r="5" /><circle cx="323" cy="94" r="5" /><circle cx="341" cy="94" r="5" /><circle cx="305" cy="111" r="5" /><circle cx="323" cy="111" r="5" /></g>
      <circle className="il-gold" cx="341" cy="111" r="5" />
      <g className="il-gold"><ellipse cx="386" cy="118" rx="20" ry="7" /><ellipse cx="386" cy="108" rx="20" ry="7" /><ellipse cx="386" cy="98" rx="20" ry="7" /></g>
    </>
  ),
  warehouse: (
    <>
      <g className="il-dark"><rect x="128" y="22" width="7" height="108" rx="2" /><rect x="304" y="22" width="7" height="108" rx="2" /><rect x="128" y="52" width="183" height="6" rx="2" /><rect x="128" y="90" width="183" height="6" rx="2" /><rect x="128" y="124" width="183" height="6" rx="2" /></g>
      <g className="il-box"><rect x="142" y="28" width="38" height="24" rx="3" /><rect x="186" y="34" width="30" height="18" rx="3" /><rect x="240" y="26" width="46" height="26" rx="3" /></g>
      <g className="il-tur"><rect x="146" y="66" width="44" height="24" rx="3" /><rect x="214" y="70" width="34" height="20" rx="3" /><rect x="256" y="64" width="38" height="26" rx="3" /></g>
      <g className="il-box"><rect x="144" y="102" width="52" height="22" rx="3" /><rect x="206" y="98" width="40" height="26" rx="3" /></g>
      <rect className="il-card" x="334" y="26" width="68" height="52" rx="7" />
      <g className="il-dark"><rect x="344" y="36" width="3" height="32" /><rect x="350" y="36" width="6" height="32" /><rect x="359" y="36" width="3" height="32" /><rect x="365" y="36" width="2" height="32" /><rect x="370" y="36" width="6" height="32" /><rect x="379" y="36" width="3" height="32" /><rect x="385" y="36" width="5" height="32" /></g>
      <rect className="il-gold" x="334" y="92" width="68" height="36" rx="5" />
      <rect className="il-pri" x="334" y="92" width="68" height="9" rx="4" />
    </>
  ),
  sales: (
    <>
      <rect className="il-dark" x="146" y="20" width="112" height="110" rx="9" />
      <rect className="il-card" x="155" y="29" width="94" height="92" rx="5" />
      <rect className="il-gold" x="165" y="38" width="40" height="8" rx="3" />
      <g className="il-mint"><rect x="165" y="56" width="74" height="6" rx="3" /><rect x="165" y="68" width="74" height="6" rx="3" /><rect x="165" y="80" width="52" height="6" rx="3" /></g>
      <rect className="il-pri" x="165" y="98" width="74" height="12" rx="4" />
      <path className="il-gold" d="M268 118 L322 52 L334 60 L280 126 Z" />
      <path className="il-dark" d="M268 118 L264 134 L280 126 Z" />
      <path className="il-stroke-pri" d="M350 44 h14 l12 50 h38 l10 -34 h-58" />
      <circle className="il-pri" cx="380" cy="108" r="7" /><circle className="il-pri" cx="408" cy="108" r="7" />
      <g className="il-tur"><rect x="372" y="64" width="12" height="18" rx="2" /><rect x="388" y="58" width="12" height="24" rx="2" /></g>
    </>
  ),
  purchases: (
    <>
      <path className="il-box" d="M150 70 h96 v58 a6 6 0 0 1 -6 6 h-84 a6 6 0 0 1 -6 -6z" />
      <path className="il-dark" d="M140 56 l30 14 h-30z M256 56 l-30 14 h30z" />
      <rect className="il-pri" x="150" y="70" width="96" height="10" />
      <path className="il-stroke-tur" d="M198 18 v40 M198 58 l-14 -14 M198 58 l14 -14" />
      <path className="il-pri" d="M292 64 h96 l-8 64 h-80z" />
      <path className="il-stroke-dark" d="M316 64 a24 24 0 0 1 48 0" />
      <circle className="il-mint" cx="340" cy="94" r="12" />
      <rect className="il-gold" x="334" y="88" width="12" height="12" rx="2" />
    </>
  ),
  reports: (
    <>
      <path className="il-stroke-dark" d="M140 26 V126 H330" />
      <g className="il-tur"><rect x="156" y="88" width="24" height="38" rx="3" /><rect x="190" y="68" width="24" height="58" rx="3" /></g>
      <g className="il-pri"><rect x="224" y="52" width="24" height="74" rx="3" /><rect x="258" y="36" width="24" height="90" rx="3" /></g>
      <path className="il-stroke-gold" d="M164 76 L202 58 L236 42 L300 26" />
      <circle className="il-mint" cx="372" cy="70" r="34" />
      <circle className="il-donut" cx="372" cy="70" r="25" strokeDasharray="95 157" transform="rotate(-90 372 70)" />
    </>
  ),
};

export function ModuleIllustration({ kind, className = "" }: { kind: IllustrationKind; className?: string }) {
  return (
    <svg className={`module-illustration ${className}`.trim()} viewBox="0 0 420 140" aria-hidden="true" focusable="false">
      <path className="il-wave" d="M0 140 C 70 50, 170 120, 250 78 S 380 30, 420 60 V140Z" />
      {ART[kind]}
    </svg>
  );
}
