import {
  readCredentials,
  serverEntry,
  saveLogin,
  request,
  authHeader,
  device,
  sameUser,
} from "./web.js";

const status = document.querySelector("#status");
const title = document.querySelector("#completion-title");
const continueLink = document.querySelector("#continue");
const backLink = document.querySelector("#back");
const show = (state) => (document.body.dataset.state = state);
try {
  const data = JSON.parse(document.querySelector("#sso-data").textContent);
  const baseUrl = location.origin + data.basePath;
  const credentials = readCredentials();
  // Verify storage is writable before consuming the one-use server completion code.
  localStorage.setItem("jellyfin_credentials", JSON.stringify(credentials));
  const info = await request(`${baseUrl}/System/Info/Public`);
  const client = device();
  const provider = encodeURIComponent(data.provider);
  if (data.target) {
    const session = serverEntry(credentials, info, baseUrl);
    if (!session || !sameUser(session.UserId, data.target))
      throw new Error(
        "The local Jellyfin account changed during linking. Sign in to the original account and start again.",
      );
    await request(
      `${baseUrl}/sso/${data.mode}/Link/${provider}/${encodeURIComponent(data.target)}`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...authHeader(session.AccessToken),
        },
        body: JSON.stringify({ ...client, Data: data.code }),
      },
    );
    show("success");
    title.textContent = "Account connected";
    status.textContent =
      "You can now sign in to this account with your identity provider.";
    continueLink.href = `${baseUrl}/SSOViews/linking`;
    continueLink.textContent = "Manage connections";
    continueLink.hidden = false;
  } else {
    const result = await request(
      `${baseUrl}/sso/${data.mode}/Auth/${provider}`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...client, Data: data.code }),
      },
    );
    try {
      saveLogin(credentials, info, result, baseUrl);
    } catch (error) {
      try {
        await request(`${baseUrl}/Sessions/Logout`, {
          method: "POST",
          headers: authHeader(result?.AccessToken),
        });
      } catch {
        // Cleanup must not hide the response-validation or storage error.
      }
      throw error;
    }
    show("success");
    title.textContent = "You’re signed in";
    status.textContent = "Opening Jellyfin…";
    continueLink.href = `${baseUrl}/web/#/home`;
    continueLink.hidden = false;
    location.replace(continueLink.href);
  }
} catch (error) {
  show("error");
  title.textContent = "Couldn’t finish signing in";
  status.setAttribute("role", "alert");
  backLink.hidden = false;
  status.textContent =
    error instanceof Error
      ? error.message
      : "Sign-in failed. Start again from Jellyfin.";
}
