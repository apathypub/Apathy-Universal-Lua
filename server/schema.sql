-- Tables used by the apathy client API (MySQL / MariaDB). Run once on the site's database.
-- Your existing users table is left alone; the API only reads it (see config.php).

CREATE TABLE IF NOT EXISTS apathy_client_sessions (
    token_hash   CHAR(64)     NOT NULL PRIMARY KEY,  -- SHA-256 of the token; the token itself is never stored
    user_id      BIGINT       NOT NULL,
    hwid         CHAR(64)     NOT NULL DEFAULT '',
    created_at   DATETIME     NOT NULL,
    last_seen_at DATETIME     NOT NULL,
    expires_at   DATETIME     NOT NULL,
    ip           VARCHAR(45)  NOT NULL DEFAULT '',
    INDEX (user_id)
);

-- The PCs (hwid) each account is bound to. A login from an unknown PC is refused once the account is
-- at its device limit, so sharing a login and password doesn't let a third party in from another PC.
CREATE TABLE IF NOT EXISTS apathy_client_devices (
    user_id    BIGINT       NOT NULL,
    hwid       CHAR(64)     NOT NULL,
    label      VARCHAR(190) NOT NULL DEFAULT '',
    created_at DATETIME     NOT NULL,
    last_seen_at DATETIME   NOT NULL,
    PRIMARY KEY (user_id, hwid)
);

CREATE TABLE IF NOT EXISTS apathy_client_attempts (
    id         BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    ip         VARCHAR(45)  NOT NULL,
    username   VARCHAR(190) NOT NULL,
    created_at DATETIME     NOT NULL,
    INDEX (ip, created_at),
    INDEX (username, created_at)
);
