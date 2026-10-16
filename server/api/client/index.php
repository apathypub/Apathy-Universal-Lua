<?php
// apathy client API: lets the desktop app log in with a site account that has premium.
// See docs/apathy-pub-api.md for the requests and answers.
declare(strict_types=1);

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

$config = require dirname(__DIR__, 2) . '/config.php';

function answer(int $status, array $body): never
{
    http_response_code($status);
    echo json_encode($body, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

function now(): string
{
    return gmdate('Y-m-d H:i:s');
}

try {
    $db = new PDO($config['db_dsn'], $config['db_user'], $config['db_pass'], [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
    ]);
    if ($db->getAttribute(PDO::ATTR_DRIVER_NAME) === 'mysql') $db->exec("SET time_zone = '+00:00'");
} catch (PDOException) {
    answer(503, ['error' => 'The login server is unavailable. Try again later.']);
}

$ip = $_SERVER['REMOTE_ADDR'] ?? '';
$action = $_GET['action'] ?? basename(parse_url($_SERVER['REQUEST_URI'] ?? '', PHP_URL_PATH) ?: '');
$method = $_SERVER['REQUEST_METHOD'] ?? 'GET';

// Identifiers from config.php are put into SQL, so only allow plain names.
function col(array $config, string $key): ?string
{
    $name = $config[$key] ?? null;
    if ($name === null) return null;
    if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $name)) throw new RuntimeException("bad column name in $key");
    return "`$name`";
}

function user_columns(array $config): string
{
    $cols = [col($config, 'col_id') . ' AS id', col($config, 'col_username') . ' AS username',
             col($config, 'col_password') . ' AS password_hash', col($config, 'col_premium_until') . ' AS premium_until'];
    if ($c = col($config, 'col_premium_lifetime')) $cols[] = "$c AS premium_lifetime";
    if ($c = col($config, 'col_banned')) $cols[] = "$c AS banned";
    return implode(', ', $cols);
}

// What the app gets back for an account; 403 when it has no active premium.
function account_answer(array $user, string $token): never
{
    if (!empty($user['banned'])) answer(403, ['error' => 'This account is banned.']);

    $lifetime = !empty($user['premium_lifetime']);
    $until = $user['premium_until'] ? strtotime($user['premium_until'] . ' UTC') : null;
    if (!$lifetime && ($until === null || $until <= time())) {
        answer(403, ['error' => 'This account doesn\'t have an active premium.']);
    }

    answer(200, [
        'username' => $user['username'],
        'token' => $token,
        'premium' => true,
        'premium_until' => $lifetime ? null : gmdate('Y-m-d\TH:i:s\Z', $until),
    ]);
}

function bearer_token(): string
{
    $header = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '';
    return preg_match('/^Bearer\s+([A-Za-z0-9_-]{20,200})$/', $header, $m) ? $m[1] : '';
}

$users = col($config, 'users_table');
$idCol = col($config, 'col_id');

switch ("$method $action") {
    case 'POST login':
        $input = json_decode(file_get_contents('php://input') ?: '', true);
        $login = trim((string)($input['username'] ?? ''));
        $password = (string)($input['password'] ?? '');
        $hwid = preg_match('/^[0-9a-f]{64}$/', (string)($input['hwid'] ?? '')) ? $input['hwid'] : '';
        if ($login === '' || $password === '') answer(400, ['error' => 'Enter your username and password.']);

        // Too many failures from this IP or for this name: slow guessing down.
        $since = gmdate('Y-m-d H:i:s', time() - $config['attempt_window']);
        $q = $db->prepare('SELECT COUNT(*) FROM apathy_client_attempts WHERE created_at > ? AND (ip = ? OR username = ?)');
        $q->execute([$since, $ip, $login]);
        if ((int)$q->fetchColumn() >= $config['max_attempts']) {
            answer(429, ['error' => 'Too many attempts. Wait a few minutes and try again.']);
        }

        $where = col($config, 'col_username') . ' = ?';
        $args = [$login];
        if ($email = col($config, 'col_email')) {
            $where .= " OR $email = ?";
            $args[] = $login;
        }
        $q = $db->prepare('SELECT ' . user_columns($config) . " FROM $users WHERE $where LIMIT 1");
        $q->execute($args);
        $user = $q->fetch();

        if (!$user || !$config['verify_password']($password, (string)$user['password_hash'])) {
            $db->prepare('INSERT INTO apathy_client_attempts (ip, username, created_at) VALUES (?, ?, ?)')
               ->execute([$ip, $login, now()]);
            answer(401, ['error' => 'Invalid username or password.']);
        }

        $db->prepare('DELETE FROM apathy_client_sessions WHERE expires_at < ?')->execute([now()]);

        // HWID lock: bind the account to this PC, or refuse when it's a different PC over the limit.
        // This is what stops a shared login and password from working on someone else's PC.
        if (($config['max_pcs'] ?? 0) > 0) {
            if ($hwid === '') {
                answer(400, ['error' => 'This copy of apathy couldn\'t identify your PC. Reinstall it, or contact support.']);
            }

            // Free PCs that haven't been used in device_forget_days (0 = never).
            if (($config['device_forget_days'] ?? 0) > 0) {
                $cutoff = gmdate('Y-m-d H:i:s', time() - $config['device_forget_days'] * 86400);
                $db->prepare('DELETE FROM apathy_client_devices WHERE user_id = ? AND last_seen_at < ?')
                   ->execute([$user['id'], $cutoff]);
            }

            $q = $db->prepare('SELECT hwid FROM apathy_client_devices WHERE user_id = ?');
            $q->execute([$user['id']]);
            $devices = $q->fetchAll(PDO::FETCH_COLUMN);

            if (!in_array($hwid, $devices, true)) {
                if (count($devices) >= $config['max_pcs']) {
                    // The login is correct, but this PC isn't one of the account's PCs.
                    answer(409, ['error' => $config['max_pcs'] === 1
                        ? 'This account is already locked to another PC. One account is for one PC only — don\'t share your login. To move to a new PC, reset it on apathy.pub.'
                        : 'This account is already used on the maximum number of PCs. Reset a PC on apathy.pub to add this one.']);
                }
                $db->prepare('INSERT INTO apathy_client_devices (user_id, hwid, created_at, last_seen_at) VALUES (?, ?, ?, ?)')
                   ->execute([$user['id'], $hwid, now(), now()]);
            } else {
                $db->prepare('UPDATE apathy_client_devices SET last_seen_at = ? WHERE user_id = ? AND hwid = ?')
                   ->execute([now(), $user['id'], $hwid]);
            }
        }

        $token = rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '=');
        $expires = gmdate('Y-m-d H:i:s', time() + $config['session_days'] * 86400);
        $db->prepare('INSERT INTO apathy_client_sessions (token_hash, user_id, hwid, created_at, last_seen_at, expires_at, ip)
                      VALUES (?, ?, ?, ?, ?, ?, ?)')
           ->execute([hash('sha256', $token), $user['id'], $hwid, now(), now(), $expires, $ip]);
        account_answer($user, $token);

    case 'GET session':
        $token = bearer_token();
        if ($token === '') answer(401, ['error' => 'Not logged in.']);
        $q = $db->prepare('SELECT ' . user_columns($config) . " FROM apathy_client_sessions s
                           JOIN $users u ON u.$idCol = s.user_id
                           WHERE s.token_hash = ? AND s.expires_at > ?");
        $q->execute([hash('sha256', $token), now()]);
        $user = $q->fetch();
        if (!$user) answer(401, ['error' => 'Your session has expired. Log in again.']);

        // Using a session keeps it alive for another session_days.
        $db->prepare('UPDATE apathy_client_sessions SET last_seen_at = ?, expires_at = ?, ip = ? WHERE token_hash = ?')
           ->execute([now(), gmdate('Y-m-d H:i:s', time() + $config['session_days'] * 86400), $ip, hash('sha256', $token)]);
        $db->prepare('UPDATE apathy_client_devices SET last_seen_at = ? WHERE user_id = ? AND hwid = (SELECT hwid FROM apathy_client_sessions WHERE token_hash = ?)')
           ->execute([now(), $user['id'], hash('sha256', $token)]);
        account_answer($user, $token);

    case 'POST logout':
        $token = bearer_token();
        if ($token !== '') {
            $db->prepare('DELETE FROM apathy_client_sessions WHERE token_hash = ?')->execute([hash('sha256', $token)]);
        }
        answer(200, ['ok' => true]);

    default:
        answer(404, ['error' => 'Unknown request.']);
}
