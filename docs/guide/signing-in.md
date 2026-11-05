# Signing in

How to sign in to Carnival App, set up your account, and get back in when something goes wrong. The last section is for system administrators, who add and look after the accounts.

Your school signs in one of two ways, and many schools use both:

- **A username and password made here**, such as `jsmith`. Your school gives you a setup link to choose the password.
- **Your school email**, if your school signs in through Microsoft (or its own sign-in page). You sign in there as you do for other school systems.

## Signing in

1. Open the app and press **Sign in**.
2. Type your username, or your school email, and press **Continue**.
3. If you typed a school email, the app takes you to your school's sign-in page and then back here.
4. If you typed a username, enter your password and press **Sign in**.
5. If you have set up an authenticator app, type the 6-digit code it shows now and press **Sign in**. No phone with you? Press **Use a recovery code instead** and type one of your recovery codes.

Wrong name? Press **Not you?** to start again.

Signing in needs the server: if the device can't reach it, reconnect and try again. A busy server may ask you to wait a few seconds.

Signing in on a device that someone else is already signed in to takes the device over. Their unsent results stay on the device and are still sent under their name.

## Setting up your account for the first time

An administrator gives you a **setup link**, in person, as a QR code, or by message. It works once and lasts 24 hours.

1. Open the link. The page is headed **Set up your account** and shows your username.
2. Choose a password: **at least 15 characters**. A short sentence works well, such as four unrelated words. It needs no symbols or numbers, and it never expires. The app refuses passwords that are common, or made mostly of the school's name, a house name or your own name.
3. Type it again and press **Save password**. You are now signed in.
4. The app offers an authenticator app. Press **Set up an authenticator app (recommended)**, or **Skip for now** and do it later on My account. If your school requires one, you can't skip it.

If the link says it has expired or has already been used, ask a system administrator for a new one. Opening a link without saving a password doesn't use it up.

## Setting up an authenticator app

An authenticator app on your phone makes a new 6-digit code every 30 seconds. With it, someone who learns your password still can't sign in as you. It needs no phone number or email.

1. Install an authenticator app if you don't have one: Microsoft Authenticator, Google Authenticator, the iPhone's Passwords app or similar.
2. Start the setup: when you first set up your account, at sign-in if your school requires it, or later under **My account** (the link at the top of the screen when you are signed in). My account asks for your password first.
3. In the authenticator app, add an account and scan the QR code on the screen. Can't scan it? Type the key shown under the code into the app instead.
4. Type the 6-digit code the app now shows and press **Confirm**.
5. The app shows your **recovery codes** (below). Save them, then press **I've saved them — continue**.

From then on, signing in asks for a code after your password.

## Recovery codes

You get ten recovery codes when you set up the authenticator app. Each one signs you in **once** in place of a code from your phone, for when your phone is lost, flat or left at home.

- They are shown **only once**. Save them straight away: in a password manager, or printed and kept locked away. **Copy codes** copies them all.
- To use one, press **Use a recovery code instead** at sign-in and type it.
- **My account** shows how many you have left.
- Run out, or lost your phone and your codes? Ask a system administrator to reset your account (below).

## Changing your password

Under **My account → Change password**, type your current password, then the new one twice, and press **Change password**. This signs you out on every other device; the one you are using stays signed in.

## Locked out, forgotten, or lost your phone

| What happened | What to do |
|---|---|
| "That username and password don't match, or this account can't sign in right now" | Check the username and password and try again. The same message appears when the account is locked, so the app never says which |
| Too many wrong tries | After 10 wrong passwords or codes within 15 minutes, the account is locked **from that network** for about 15 minutes. Wait 15 minutes, or sign in from another network (for example your phone on mobile data), or ask a system administrator to clear the lockout. Staff signing in correctly from the same network are not affected |
| "That code didn't work" | Codes change every 30 seconds: type the one the app shows now. Check the phone's clock is set automatically |
| Forgot your password | Ask a system administrator to reset your account. You get a new setup link and choose a new password. A reset also removes your authenticator app, so set it up again |
| Lost your phone | Sign in with a recovery code, then ask a system administrator to reset your account so you can set up the new phone |
| "This account can't sign in here" | At this school only system administrators use a username and password. Sign in with your school email instead |

## On shared devices

- Sign out when you hand a device over: the **Sign out** link at the top. It also clears the cached rosters from the device.
- After 60 minutes with no taps, the app signs the device out. Sign in again with your password (and code). If the server can't be reached at that moment, the app covers the screen with a "Screen locked" cover instead, so the cached roster isn't lost, and signs out as soon as the network returns.
- A sign-in lasts at most 12 hours, however busy the day.

## For system administrators: School → People

**School → People** lists the accounts that sign in with a username and password here. Only system administrators see it.

**Add a person**

1. Under **Add a person**, enter a username (a plain name such as `jsmith`: lower-case letters, digits, dots, dashes and underscores, never an email address) and a display name, and press **Add person**.
2. The app shows the person's **setup link** once, with a QR code. Let them scan it, or press **Copy link** and send it by message. It works once and lasts 24 hours.
3. Give them a role under **School → Roles**, using their username. Without a role they can sign in but can't see or change anything.

**Look after an account**

Each row shows the person's status, whether they have an authenticator app, whether they are locked out, when they last signed in, and when and from where their setup link was used.

| Button | Does |
|---|---|
| **Clear lockout** | Shown when the account is locked out. Lets them try again straight away, from any network |
| **Reset…** | Clears their password, authenticator app and recovery codes, signs them out everywhere, and makes a new setup link. Use it for a forgotten password, a lost phone, or a setup link that went to the wrong person (the new link stops the old one working) |
| **Disable…** | Stops them signing in, and signs them out the next time their device reaches the server. Accounts are never deleted |
| **Enable** | Lets a disabled person sign in again with their old password. To reset a disabled account, enable it first |

You can't change your own account here; ask another system administrator, or use My account for your own password.

**Sign-in settings** are under **School → School settings → Sign-in**: whether everyone must set up an authenticator app, and, if the school also signs in through Microsoft, whether local accounts are for system administrators only or for everyone.

**Keep a backup administrator.** If your school signs in through Microsoft, keep at least one system administrator with a local account, an authenticator app, and its password and recovery codes in the school's safe or password manager. It still works when the school's sign-in is down. If every administrator is locked out, whoever runs the server can make a new setup link from the host ([self-hosting](../admin/self-host.md#recovering-access-from-the-host)).
