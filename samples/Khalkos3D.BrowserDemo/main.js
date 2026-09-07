// Boots the runtime and hands the page over to C#. Everything shown in the tab is written by
// Page.cs through the one import below — the JavaScript here knows nothing about models.
import { dotnet } from './_framework/dotnet.js'

const { setModuleImports, runMain } = await dotnet.create();

setModuleImports('main.js', {
    page: {
        row: (name, value, bad) => {
            const out = document.getElementById('out');
            const dt = document.createElement('dt');
            const dd = document.createElement('dd');
            dt.textContent = name;
            dd.textContent = value;
            if (bad) dd.className = 'bad';
            out.append(dt, dd);
        },
        clear: () => { document.getElementById('out').replaceChildren(); },
        note: html => { document.getElementById('why').innerHTML = html; },
    },
});

await runMain();
