# Cluck In frontend demo

A standalone React + TypeScript + Vite dashboard for the hackathon. All data is static: modes are display-only, the timer does not count down, and there are no backend calls.

## Run

Use Node.js 22.12+ (or a newer supported LTS version) with npm. From the repository root:

```bash
cd src/web
npm install
npm run dev
```

Open the local URL printed by Vite (normally http://localhost:5173).

## Build

```bash
npm run build
npm run preview
```

`npm run build` checks TypeScript and creates `dist/`. `npm run preview` serves that build locally.

Edit the mock data in `src/App.tsx` and the styles in `src/styles.css`. This module runs independently of the .NET and Python projects.
