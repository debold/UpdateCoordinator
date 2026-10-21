# Sign-in and first start {#top}

How to get into the dashboard: with a local account, with your Windows account, and the one-time setup of the first administrator.

## Sign in {#sign-in}

- **User name** and **Password** of a local account, then **Sign in**. After five failed attempts the account is locked for 15 minutes; the message does not say whether the name or the password was wrong.
- **Sign in with Windows**: shown when Windows sign-in is switched on ([Users](help/users#windows)). On a computer in the domain the browser signs you in with your Windows account without a password; otherwise it asks for your Windows credentials. Your role comes from your AD groups; if you are in no mapped group, sign-in is refused.

The session ends after 8 hours without activity, or with **Sign out** at the bottom of the navigation. The navigation also shows the version of the central server. All times in the dashboard are in the time zone of the central server.

## First start {#setup}

As long as no account exists, the sign-in page links to the setup of the first administrator, which needs a one-time setup token. The easiest way: the interactive installation of the central server shows the token on its port page (to copy), and "Open the dashboard" on its last page opens the setup with the token filled in. Otherwise the token is in a file on the central server, by default `%ProgramData%\SUSyphos\Server\setup-token.txt`, readable only by administrators; the sign-in and setup pages show the exact path while the setup is open.

- **Setup token**: filled in when the installer opened the page, else the content of that file.
- **User name** of the first administrator.
- **Password** and **Repeat password**: at least 12 characters.
- **Create administrator** creates the account with the Admin role, deletes the token file and closes the setup for good.
