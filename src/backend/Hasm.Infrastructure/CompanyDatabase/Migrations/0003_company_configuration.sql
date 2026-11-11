-- Company database migration 0003: company configuration (Phase 3.1A-2).
-- Physical identifiers come from CompanySchema (generated, opaque); this file is immutable once applied (checksummed).
-- Adds: company profile (singleton), company settings (singleton), the ISO 4217 currency reference (dataset version 1),
-- the company's enabled currencies, and the append-only configuration revision history. Existing company data is untouched.
-- Profile and settings rows are created by the first authorized write (never seeded, never created by a read), so no company-specific
-- value is assumed here. The runtime role receives its privileges from 'Hasm.Admin grant-company-runtime' (see CompanyRuntimePrivileges).

-- ---------------------------------------------------------------- profile
CREATE TABLE t_2e9d6bc3 (
    c_81d7eedc boolean NOT NULL DEFAULT true,
    c_bf187da1 varchar(200) NULL,
    c_a5039363 varchar(200) NULL,
    c_997d18d9 varchar(200) NULL,
    c_a7358ff3 varchar(64) NULL,
    c_61272f02 varchar(64) NULL,
    c_ff810b4a char(2) NOT NULL,
    c_fa7b1c20 varchar(200) NULL,
    c_2aafdf1e varchar(200) NULL,
    c_f500049e varchar(100) NULL,
    c_077dc782 varchar(100) NULL,
    c_5108587b varchar(20) NULL,
    c_54f337be varchar(30) NULL,
    c_d4b4f263 varchar(254) NULL,
    c_f6e709ac varchar(200) NULL,
    c_ab911ce6 bigint NOT NULL DEFAULT 1,
    c_099a7c10 timestamptz NOT NULL,
    CONSTRAINT k_2e0faa9f PRIMARY KEY (c_81d7eedc),
    CONSTRAINT k_7aaf5b2c CHECK (c_81d7eedc),
    CONSTRAINT k_05daa1e6 CHECK (
        (c_bf187da1 IS NOT NULL OR c_a5039363 IS NOT NULL)
        AND (c_bf187da1 IS NULL OR char_length(btrim(c_bf187da1)) BETWEEN 1 AND 200)
        AND (c_a5039363 IS NULL OR char_length(btrim(c_a5039363)) BETWEEN 1 AND 200)),
    CONSTRAINT k_abb696f4 CHECK (c_ff810b4a ~ '^[A-Z]{2}$'),
    CONSTRAINT k_1d956de0 CHECK (c_ab911ce6 > 0)
);

-- ---------------------------------------------------------------- settings
CREATE TABLE t_91632385 (
    c_17f711ae boolean NOT NULL DEFAULT true,
    c_6018782a varchar(2) NOT NULL DEFAULT 'ar',
    c_c50f3112 varchar(10) NOT NULL DEFAULT 'yyyy-MM-dd',
    c_d707e413 varchar(12) NOT NULL DEFAULT 'latin',
    -- NULL = setup incomplete: no business time zone is ever assumed.
    c_05e0a481 varchar(64) NULL,
    c_958e8cfd bigint NOT NULL DEFAULT 1,
    c_bd5ed7d9 timestamptz NOT NULL,
    CONSTRAINT k_2b1c41d0 PRIMARY KEY (c_17f711ae),
    CONSTRAINT k_d02830a5 CHECK (c_17f711ae),
    CONSTRAINT k_5c6c576e CHECK (
        c_6018782a IN ('ar', 'en')
        AND c_c50f3112 IN ('yyyy-MM-dd', 'dd/MM/yyyy', 'MM/dd/yyyy')
        AND c_d707e413 IN ('latin', 'arabic-indic')
        AND (c_05e0a481 IS NULL OR c_05e0a481 ~ '^[A-Za-z0-9_+/-]{1,64}$')),
    CONSTRAINT k_6d29eeea CHECK (c_958e8cfd > 0)
);

-- ---------------------------------------------------------------- currency reference (ISO 4217)
CREATE TABLE t_451e91be (
    c_b6064cf1 char(3) NOT NULL,
    c_e4b41998 char(3) NOT NULL,
    c_0a598299 varchar(100) NOT NULL,
    -- NULL where ISO 4217 defines no minor-unit exponent: never invented.
    c_b037f3d5 smallint NULL,
    c_f8d3a05a boolean NOT NULL DEFAULT true,
    c_4b13e59d integer NOT NULL,
    CONSTRAINT k_e1984a82 PRIMARY KEY (c_b6064cf1),
    CONSTRAINT k_c72623fe UNIQUE (c_e4b41998),
    CONSTRAINT k_091aedd2 CHECK (c_b6064cf1 ~ '^[A-Z]{3}$' AND c_e4b41998 ~ '^[0-9]{3}$'
        AND (c_b037f3d5 IS NULL OR c_b037f3d5 BETWEEN 0 AND 4) AND c_4b13e59d >= 1)
);

-- Reviewed ISO 4217 reference dataset, version 1 (to be re-verified against the current ISO amendment before release).
INSERT INTO t_451e91be (c_b6064cf1, c_e4b41998, c_0a598299, c_b037f3d5, c_f8d3a05a, c_4b13e59d) VALUES
    ('AED', '784', 'UAE Dirham', 2, true, 1),
    ('AFN', '971', 'Afghani', 2, true, 1),
    ('ALL', '008', 'Lek', 2, true, 1),
    ('AMD', '051', 'Armenian Dram', 2, true, 1),
    ('ANG', '532', 'Netherlands Antillean Guilder', 2, true, 1),
    ('AOA', '973', 'Kwanza', 2, true, 1),
    ('ARS', '032', 'Argentine Peso', 2, true, 1),
    ('AUD', '036', 'Australian Dollar', 2, true, 1),
    ('AWG', '533', 'Aruban Florin', 2, true, 1),
    ('AZN', '944', 'Azerbaijan Manat', 2, true, 1),
    ('BAM', '977', 'Convertible Mark', 2, true, 1),
    ('BBD', '052', 'Barbados Dollar', 2, true, 1),
    ('BDT', '050', 'Taka', 2, true, 1),
    ('BGN', '975', 'Bulgarian Lev', 2, true, 1),
    ('BHD', '048', 'Bahraini Dinar', 3, true, 1),
    ('BIF', '108', 'Burundi Franc', 0, true, 1),
    ('BMD', '060', 'Bermudian Dollar', 2, true, 1),
    ('BND', '096', 'Brunei Dollar', 2, true, 1),
    ('BOB', '068', 'Boliviano', 2, true, 1),
    ('BRL', '986', 'Brazilian Real', 2, true, 1),
    ('BSD', '044', 'Bahamian Dollar', 2, true, 1),
    ('BTN', '064', 'Ngultrum', 2, true, 1),
    ('BWP', '072', 'Pula', 2, true, 1),
    ('BYN', '933', 'Belarusian Ruble', 2, true, 1),
    ('BZD', '084', 'Belize Dollar', 2, true, 1),
    ('CAD', '124', 'Canadian Dollar', 2, true, 1),
    ('CDF', '976', 'Congolese Franc', 2, true, 1),
    ('CHF', '756', 'Swiss Franc', 2, true, 1),
    ('CLP', '152', 'Chilean Peso', 0, true, 1),
    ('CNY', '156', 'Yuan Renminbi', 2, true, 1),
    ('COP', '170', 'Colombian Peso', 2, true, 1),
    ('CRC', '188', 'Costa Rican Colon', 2, true, 1),
    ('CUP', '192', 'Cuban Peso', 2, true, 1),
    ('CVE', '132', 'Cabo Verde Escudo', 2, true, 1),
    ('CZK', '203', 'Czech Koruna', 2, true, 1),
    ('DJF', '262', 'Djibouti Franc', 0, true, 1),
    ('DKK', '208', 'Danish Krone', 2, true, 1),
    ('DOP', '214', 'Dominican Peso', 2, true, 1),
    ('DZD', '012', 'Algerian Dinar', 2, true, 1),
    ('EGP', '818', 'Egyptian Pound', 2, true, 1),
    ('ERN', '232', 'Nakfa', 2, true, 1),
    ('ETB', '230', 'Ethiopian Birr', 2, true, 1),
    ('EUR', '978', 'Euro', 2, true, 1),
    ('FJD', '242', 'Fiji Dollar', 2, true, 1),
    ('FKP', '238', 'Falkland Islands Pound', 2, true, 1),
    ('GBP', '826', 'Pound Sterling', 2, true, 1),
    ('GEL', '981', 'Lari', 2, true, 1),
    ('GHS', '936', 'Ghana Cedi', 2, true, 1),
    ('GIP', '292', 'Gibraltar Pound', 2, true, 1),
    ('GMD', '270', 'Dalasi', 2, true, 1),
    ('GNF', '324', 'Guinean Franc', 0, true, 1),
    ('GTQ', '320', 'Quetzal', 2, true, 1),
    ('GYD', '328', 'Guyana Dollar', 2, true, 1),
    ('HKD', '344', 'Hong Kong Dollar', 2, true, 1),
    ('HNL', '340', 'Lempira', 2, true, 1),
    ('HTG', '332', 'Gourde', 2, true, 1),
    ('HUF', '348', 'Forint', 2, true, 1),
    ('IDR', '360', 'Rupiah', 2, true, 1),
    ('ILS', '376', 'New Israeli Sheqel', 2, true, 1),
    ('INR', '356', 'Indian Rupee', 2, true, 1),
    ('IQD', '368', 'Iraqi Dinar', 3, true, 1),
    ('IRR', '364', 'Iranian Rial', 2, true, 1),
    ('ISK', '352', 'Iceland Krona', 0, true, 1),
    ('JMD', '388', 'Jamaican Dollar', 2, true, 1),
    ('JOD', '400', 'Jordanian Dinar', 3, true, 1),
    ('JPY', '392', 'Yen', 0, true, 1),
    ('KES', '404', 'Kenyan Shilling', 2, true, 1),
    ('KGS', '417', 'Som', 2, true, 1),
    ('KHR', '116', 'Riel', 2, true, 1),
    ('KMF', '174', 'Comorian Franc', 0, true, 1),
    ('KPW', '408', 'North Korean Won', 2, true, 1),
    ('KRW', '410', 'Won', 0, true, 1),
    ('KWD', '414', 'Kuwaiti Dinar', 3, true, 1),
    ('KYD', '136', 'Cayman Islands Dollar', 2, true, 1),
    ('KZT', '398', 'Tenge', 2, true, 1),
    ('LAK', '418', 'Lao Kip', 2, true, 1),
    ('LBP', '422', 'Lebanese Pound', 2, true, 1),
    ('LKR', '144', 'Sri Lanka Rupee', 2, true, 1),
    ('LRD', '430', 'Liberian Dollar', 2, true, 1),
    ('LSL', '426', 'Loti', 2, true, 1),
    ('LYD', '434', 'Libyan Dinar', 3, true, 1),
    ('MAD', '504', 'Moroccan Dirham', 2, true, 1),
    ('MDL', '498', 'Moldovan Leu', 2, true, 1),
    ('MGA', '969', 'Malagasy Ariary', 2, true, 1),
    ('MKD', '807', 'Denar', 2, true, 1),
    ('MMK', '104', 'Kyat', 2, true, 1),
    ('MNT', '496', 'Tugrik', 2, true, 1),
    ('MOP', '446', 'Pataca', 2, true, 1),
    ('MRU', '929', 'Ouguiya', 2, true, 1),
    ('MUR', '480', 'Mauritius Rupee', 2, true, 1),
    ('MVR', '462', 'Rufiyaa', 2, true, 1),
    ('MWK', '454', 'Malawi Kwacha', 2, true, 1),
    ('MXN', '484', 'Mexican Peso', 2, true, 1),
    ('MYR', '458', 'Malaysian Ringgit', 2, true, 1),
    ('MZN', '943', 'Mozambique Metical', 2, true, 1),
    ('NAD', '516', 'Namibia Dollar', 2, true, 1),
    ('NGN', '566', 'Naira', 2, true, 1),
    ('NIO', '558', 'Cordoba Oro', 2, true, 1),
    ('NOK', '578', 'Norwegian Krone', 2, true, 1),
    ('NPR', '524', 'Nepalese Rupee', 2, true, 1),
    ('NZD', '554', 'New Zealand Dollar', 2, true, 1),
    ('OMR', '512', 'Rial Omani', 3, true, 1),
    ('PAB', '590', 'Balboa', 2, true, 1),
    ('PEN', '604', 'Sol', 2, true, 1),
    ('PGK', '598', 'Kina', 2, true, 1),
    ('PHP', '608', 'Philippine Peso', 2, true, 1),
    ('PKR', '586', 'Pakistan Rupee', 2, true, 1),
    ('PLN', '985', 'Zloty', 2, true, 1),
    ('PYG', '600', 'Guarani', 0, true, 1),
    ('QAR', '634', 'Qatari Rial', 2, true, 1),
    ('RON', '946', 'Romanian Leu', 2, true, 1),
    ('RSD', '941', 'Serbian Dinar', 2, true, 1),
    ('RUB', '643', 'Russian Ruble', 2, true, 1),
    ('RWF', '646', 'Rwanda Franc', 0, true, 1),
    ('SAR', '682', 'Saudi Riyal', 2, true, 1),
    ('SBD', '090', 'Solomon Islands Dollar', 2, true, 1),
    ('SCR', '690', 'Seychelles Rupee', 2, true, 1),
    ('SDG', '938', 'Sudanese Pound', 2, true, 1),
    ('SEK', '752', 'Swedish Krona', 2, true, 1),
    ('SGD', '702', 'Singapore Dollar', 2, true, 1),
    ('SHP', '654', 'Saint Helena Pound', 2, true, 1),
    ('SLE', '925', 'Leone', 2, true, 1),
    ('SOS', '706', 'Somali Shilling', 2, true, 1),
    ('SRD', '968', 'Surinam Dollar', 2, true, 1),
    ('SSP', '728', 'South Sudanese Pound', 2, true, 1),
    ('STN', '930', 'Dobra', 2, true, 1),
    ('SVC', '222', 'El Salvador Colon', 2, true, 1),
    ('SYP', '760', 'Syrian Pound', 2, true, 1),
    ('SZL', '748', 'Lilangeni', 2, true, 1),
    ('THB', '764', 'Baht', 2, true, 1),
    ('TJS', '972', 'Somoni', 2, true, 1),
    ('TMT', '934', 'Turkmenistan New Manat', 2, true, 1),
    ('TND', '788', 'Tunisian Dinar', 3, true, 1),
    ('TOP', '776', 'Pa''anga', 2, true, 1),
    ('TRY', '949', 'Turkish Lira', 2, true, 1),
    ('TTD', '780', 'Trinidad and Tobago Dollar', 2, true, 1),
    ('TWD', '901', 'New Taiwan Dollar', 2, true, 1),
    ('TZS', '834', 'Tanzanian Shilling', 2, true, 1),
    ('UAH', '980', 'Hryvnia', 2, true, 1),
    ('UGX', '800', 'Uganda Shilling', 0, true, 1),
    ('USD', '840', 'US Dollar', 2, true, 1),
    ('UYU', '858', 'Peso Uruguayo', 2, true, 1),
    ('UZS', '860', 'Uzbekistan Sum', 2, true, 1),
    ('VES', '928', 'Bolivar Soberano', 2, true, 1),
    ('VND', '704', 'Dong', 0, true, 1),
    ('VUV', '548', 'Vatu', 0, true, 1),
    ('WST', '882', 'Tala', 2, true, 1),
    ('XAF', '950', 'CFA Franc BEAC', 0, true, 1),
    ('XCD', '951', 'East Caribbean Dollar', 2, true, 1),
    ('XDR', '960', 'SDR (Special Drawing Right)', NULL, true, 1),
    ('XOF', '952', 'CFA Franc BCEAO', 0, true, 1),
    ('XPF', '953', 'CFP Franc', 0, true, 1),
    ('YER', '886', 'Yemeni Rial', 2, true, 1),
    ('ZAR', '710', 'Rand', 2, true, 1),
    ('ZMW', '967', 'Zambian Kwacha', 2, true, 1),
    ('ZWG', '924', 'Zimbabwe Gold', 2, true, 1);

-- Reference identifiers are never removed or renumbered by the application; changes arrive as later versioned migrations.
CREATE FUNCTION f_8bc68ad5() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'currency reference rows cannot be deleted';
    END IF;
    IF NEW.c_b6064cf1 IS DISTINCT FROM OLD.c_b6064cf1 THEN
        RAISE EXCEPTION 'currency identifiers are immutable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_999aca37 BEFORE UPDATE OR DELETE ON t_451e91be
    FOR EACH ROW EXECUTE FUNCTION f_8bc68ad5();
CREATE TRIGGER r_6fee6c21 BEFORE TRUNCATE ON t_451e91be
    FOR EACH STATEMENT EXECUTE FUNCTION f_8bc68ad5();

-- ---------------------------------------------------------------- company currencies
CREATE TABLE t_f5bc326a (
    c_6da45b20 char(3) NOT NULL,
    c_5cdc1bc7 boolean NOT NULL DEFAULT false,
    c_702730ab boolean NOT NULL DEFAULT true,
    c_172cc0fb bigint NOT NULL DEFAULT 1,
    c_e6033338 timestamptz NOT NULL,
    CONSTRAINT k_b65971ee PRIMARY KEY (c_6da45b20),
    CONSTRAINT k_fc23520c FOREIGN KEY (c_6da45b20) REFERENCES t_451e91be (c_b6064cf1),
    CONSTRAINT k_e521d50a CHECK (NOT c_5cdc1bc7 OR c_702730ab),
    CONSTRAINT k_2c5b6e22 CHECK (c_172cc0fb > 0)
);
-- At most one base currency.
CREATE UNIQUE INDEX k_16957f38 ON t_f5bc326a (c_5cdc1bc7) WHERE c_5cdc1bc7;

-- ---------------------------------------------------------------- configuration revision history (append-only)
CREATE TABLE t_28e40627 (
    c_1a60a888 bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    c_63d71cfe varchar(16) NOT NULL,
    c_f9d1a1a0 varchar(3) NOT NULL DEFAULT '',
    c_06d232c5 bigint NOT NULL,
    c_f0e6a1e6 uuid NOT NULL,
    c_85d457aa timestamptz NOT NULL,
    c_3136498c jsonb NOT NULL,
    -- The authenticated business audit event written in the SAME transaction (composite key of the audit table).
    c_fda83fac varchar(48) NOT NULL,
    c_4cf0bc15 bigint NOT NULL,
    CONSTRAINT k_6027e53d PRIMARY KEY (c_1a60a888),
    CONSTRAINT k_ba3c64bd UNIQUE (c_63d71cfe, c_f9d1a1a0, c_06d232c5),
    CONSTRAINT k_00a7d2d4 UNIQUE (c_fda83fac, c_4cf0bc15),
    CONSTRAINT k_f9a03b99 FOREIGN KEY (c_fda83fac, c_4cf0bc15)
        REFERENCES t_2db8cf1f (c_839d1168, c_689dc6c4),
    CONSTRAINT k_d6ad16bf CHECK (c_63d71cfe IN ('profile', 'settings', 'currency')
        AND c_06d232c5 > 0 AND jsonb_typeof(c_3136498c) = 'object')
);

CREATE FUNCTION f_add97941() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'configuration revisions are append-only';
END;
$$;
CREATE TRIGGER r_e911d88f BEFORE UPDATE OR DELETE ON t_28e40627
    FOR EACH ROW EXECUTE FUNCTION f_add97941();
CREATE TRIGGER r_e0b16d49 BEFORE TRUNCATE ON t_28e40627
    FOR EACH STATEMENT EXECUTE FUNCTION f_add97941();

-- ---------------------------------------------------------------- singleton/company-currency row guards
-- Rows are never deleted; every update must advance the revision by exactly one (a second, database-level optimistic-concurrency fence).
CREATE FUNCTION f_8cb0c195() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'configuration rows cannot be deleted'; END IF;
    IF NEW.c_ab911ce6 <> OLD.c_ab911ce6 + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_3b873b12 BEFORE UPDATE OR DELETE ON t_2e9d6bc3 FOR EACH ROW EXECUTE FUNCTION f_8cb0c195();
CREATE TRIGGER r_de094354 BEFORE TRUNCATE ON t_2e9d6bc3 FOR EACH STATEMENT EXECUTE FUNCTION f_8cb0c195();

CREATE FUNCTION f_24d41a61() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'configuration rows cannot be deleted'; END IF;
    IF NEW.c_958e8cfd <> OLD.c_958e8cfd + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_cae49e47 BEFORE UPDATE OR DELETE ON t_91632385 FOR EACH ROW EXECUTE FUNCTION f_24d41a61();
CREATE TRIGGER r_9ad749be BEFORE TRUNCATE ON t_91632385 FOR EACH STATEMENT EXECUTE FUNCTION f_24d41a61();

CREATE FUNCTION f_383a1156() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN RAISE EXCEPTION 'configuration rows cannot be deleted'; END IF;
    IF NEW.c_6da45b20 IS DISTINCT FROM OLD.c_6da45b20 THEN RAISE EXCEPTION 'currency identifiers are immutable'; END IF;
    IF NEW.c_172cc0fb <> OLD.c_172cc0fb + 1 THEN RAISE EXCEPTION 'revision must advance by one'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER r_d6b1ff2c BEFORE UPDATE OR DELETE ON t_f5bc326a FOR EACH ROW EXECUTE FUNCTION f_383a1156();
CREATE TRIGGER r_54661fe7 BEFORE TRUNCATE ON t_f5bc326a FOR EACH STATEMENT EXECUTE FUNCTION f_383a1156();

REVOKE ALL ON TABLE t_2e9d6bc3, t_91632385, t_451e91be, t_f5bc326a, t_28e40627 FROM PUBLIC;
