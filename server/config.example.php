<?php
// Copy to config.php (next to this file, outside the public web root if you can) and fill it in.
// Never commit config.php: it holds the database password.

return [
    // Same database as the site.
    'db_dsn'  => 'mysql:host=localhost;dbname=apathy;charset=utf8mb4',
    'db_user' => 'apathy',
    'db_pass' => 'change-me',

    // Where the site keeps its accounts. Adjust the names to your schema.
    'users_table'      => 'users',
    'col_id'           => 'id',
    'col_username'     => 'username',
    'col_email'        => 'email',          // set to null if logins are by username only
    'col_password'     => 'password',       // a password_hash() / bcrypt / argon2 hash
    // Premium: a DATETIME column with the end date (NULL = no premium) ...
    'col_premium_until' => 'premium_until',
    // ... and, optionally, a 0/1 column for lifetime premium (null if there is none).
    'col_premium_lifetime' => null,
    // Optional 0/1 column for banned accounts (null if there is none).
    'col_banned' => null,

    // If the site doesn't use password_hash(), replace this with its own check.
    'verify_password' => fn(string $password, string $hash): bool => password_verify($password, $hash),

    'session_days'   => 30,  // a remembered login lasts this long without use
    // How many different PCs (hwid) one account may be BOUND to. The account locks to the first PCs that
    // log in; a login from another PC is refused (HTTP 409) until a bound PC is freed (reset on the site,
    // or 'device_forget_days' below). 1 = one PC per account; 0 = no HWID lock.
    'max_pcs'            => 1,
    'device_forget_days' => 0, // a bound PC unused for this many days frees its slot (0 = never auto-free)
    'max_attempts'   => 10,  // failed logins per IP or username ...
    'attempt_window' => 600, // ... within this many seconds
];
