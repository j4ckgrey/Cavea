<p align="center">
  <img src="cavea_name.png" alt="Cavea Logo" width="200" />
</p>

<p align="center">
  <i>
    Cavea is a media streaming Helper for Remote Streams, designed to work with Anfiteatro and Gelato, but not only, can also be used as a standlone plugin for Jellyfin due to the features it can provide.
  </i>
</p>

---

## 🍯 Overview

**Cavea** is both a **server-side powerhouse** and a **frontend overhaul** for Remote Streaming Media Cliente, enhanced to work better with Gelato Plugin, Remux Server and Anfiteatro Client.
It introduces smart caching, remote stream integration, complete user-to-admin request handling, 
and dynamic UI enhancements for effortless media management.

Whether you're streaming from remote sources or managing requests from multiple users,  
Cavea ensures your Media Streaming experience stays fast, reliable, and visually polished.

---

## ⚙️ Key Features

### 🧠 Server-Side Intelligence

See [STREAM_CACHING.md](./STREAM_CACHING.md) for detailed documentation.
#### 🧩 Smart Probing for Tracks
Automatically fetches **audio and subtitle tracks** from remote streams for every platform, including Android TV and Fire TV.  
Cavea ensures your playback options are complete before the media even starts loading.

> This makes it a perfect companion for the **Gelato** plugin — unlocking full hybrid-source playback.

---

### 📬 Complete Requests System

#### 🙋 User-to-Admin Media Requests
Cavea introduces a **fully native request system** within Jellyfin — bringing the power of Jellyseerr directly into your dashboard.  
Users can submit requests for unavailable content, track status, and get notified upon approval or import.

- Requests are stored **per user** with status tracking  
- Admins can **approve or deny** requests via a built-in interface  
- Works seamlessly with **manual imports** and **Gelato discovery**

#### ⚙️ Configurable Behavior
From the **plugin configuration page**, you can:
- Disable **Global Search Toggle** for TV clients (enforcing safe, local searches)
- Toggle **Auto Import** to allow or restrict direct imports (ideal for managing user permissions)
- Enable/disable the **Requests Feature** entirely for automation scenarios

---

### 🧭 Manual Import System

Cavea introduces a **Manual Import Modal**, acting as a **middle-layer between Gelato and Jellyfin**.  
Instead of automatically importing everything Gelato finds, Cavea opens a detailed preview modal:

- Displays **cast, metadata, reviews, and artwork**  
- Lets users confirm before import  
- Prevents redundant or accidental imports  
- Streamlines admin control
---

### 🎨 Interface Enhancements

Cavea provides a bunch of UI enhancements to make your experience better.

#### 🧱 Smart Selectors
Each version, audio, and subtitle field now uses a **responsive carousel or dropdown**, designed for clarity and speed.  
Long filenames are truncated elegantly, with hover or click revealing full details.
---

## 🧩 Installation (Currently works with Jellyfin until the first release of Remux Server)

### ✅ Via Jellyfin Plugin Repository

1. Open **Jellyfin Dashboard**  
2. Go to **Plugins → Repositories**  
3. Add: https://raw.githubusercontent.com/j4ckgrey/Cavea/main/manifest.json
4. Open **Catalog**, install **Cavea**  
5. Restart Jellyfin  

---
## 💬 Community & Support

- 🐞 **Bugs & Issues:** [GitHub Issues](https://github.com/j4ckgrey/Cavea/issues)  
- 💡 **Discussions:** [GitHub Discussions](https://discordapp.com/channels/1433689453158862943/1441378427239137300)  
- 💡 **Support:** If you like my work and want to support development, consider buying me a coffee:👉 https://ko-fi.com/j4ckgrey
---