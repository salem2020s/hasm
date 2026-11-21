import type { Catalog } from "@hasm/ui";
import { baseAr, baseEn } from "./messages.base";
import { ledgerAr, ledgerEn } from "./messages.ledger";
import { importsAr, importsEn } from "./messages.imports";
import { statementsAr, statementsEn } from "./messages.statements";
import { inventoryAr, inventoryEn } from "./messages.inventory";
import { stockAr, stockEn } from "./messages.stock";
import { commercialAr, commercialEn } from "./messages.commercial";
import { invaccAr, invaccEn } from "./messages.invacc";
import { stockbalAr, stockbalEn } from "./messages.stockbal";
import { stocktakeAr, stocktakeEn } from "./messages.stocktake";
import { negstockAr, negstockEn } from "./messages.negstock";
import { taxAr, taxEn } from "./messages.tax";
import { pricingAr, pricingEn } from "./messages.pricing";
import { retentionAr, retentionEn } from "./messages.retention";
import { customerGroupsAr, customerGroupsEn } from "./messages.customerGroups";
import { partyRolesAr, partyRolesEn } from "./messages.partyroles";
import { attachmentsAr, attachmentsEn } from "./messages.attachments";
import { impTplAr, impTplEn } from "./messages.imptpl";
import { partyAr, partyEn } from "./messages.party";
import { mfaAr, mfaEn } from "./messages.mfa";

export const catalog: Catalog = {
  ar: { ...baseAr, ...mfaAr },
  en: { ...baseEn, ...mfaEn },
};

// General Ledger screens (Phase 3.2B) live in their own module; merged here so the whole catalog stays one object.
Object.assign(catalog.ar, ledgerAr);
Object.assign(catalog.en, ledgerEn);
Object.assign(catalog.ar, importsAr);
Object.assign(catalog.en, importsEn);
Object.assign(catalog.ar, statementsAr);
Object.assign(catalog.en, statementsEn);
Object.assign(catalog.ar, inventoryAr);
Object.assign(catalog.en, inventoryEn);
Object.assign(catalog.ar, stockAr);
Object.assign(catalog.ar, commercialAr);
Object.assign(catalog.en, stockEn);
Object.assign(catalog.en, commercialEn);
Object.assign(catalog.ar, invaccAr);
Object.assign(catalog.en, invaccEn);
Object.assign(catalog.ar, stockbalAr);
Object.assign(catalog.en, stockbalEn);
Object.assign(catalog.ar, stocktakeAr);
Object.assign(catalog.en, stocktakeEn);
Object.assign(catalog.ar, negstockAr);
Object.assign(catalog.en, negstockEn);
Object.assign(catalog.ar, taxAr);
Object.assign(catalog.en, taxEn);
Object.assign(catalog.ar, pricingAr);
Object.assign(catalog.en, pricingEn);
Object.assign(catalog.ar, retentionAr);
Object.assign(catalog.en, retentionEn);
Object.assign(catalog.ar, customerGroupsAr);
Object.assign(catalog.en, customerGroupsEn);
Object.assign(catalog.ar, partyRolesAr);
Object.assign(catalog.en, partyRolesEn);
Object.assign(catalog.ar, attachmentsAr);
Object.assign(catalog.en, attachmentsEn);
Object.assign(catalog.ar, impTplAr);
Object.assign(catalog.en, impTplEn);
Object.assign(catalog.ar, partyAr);
Object.assign(catalog.en, partyEn);
