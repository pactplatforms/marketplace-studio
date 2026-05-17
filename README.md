# PACT Marketplace Studio

Upload 3D assets to the PACT AR Marketplace directly from Unity.

## Install

1. Open Unity → Window → Package Manager
2. Click **+** → **Add package from git URL**
3. Paste: `https://github.com/pactplatforms/marketplace-studio.git`

## Usage

1. Open **Pact → Marketplace Studio** from the menu bar
2. Drag your Prefab or FBX into the 3D Model field
3. Enter your Asset ID, email, and display name
4. Click **BUILD & PUBLISH**
5. Check your email for the verification link

## Requirements

- Unity 2021.3 or later
- iOS Build Support module installed

## What it does

- Builds an iOS AssetBundle from any Prefab or FBX
- Validates triangle count before upload — max 100,000
- Retry logic with exponential backoff on all network calls
- Uploads directly to PACT CDN via secure presigned URLs
- Assets auto-expire after 1 hour if not verified
- 1024MB Lambda backend — sub-2 second processing
