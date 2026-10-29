// Syntax check for the C# sources without a .NET SDK.
//
// This sandbox has no dotnet/mono, so a change to the bot could only be checked
// by building it on the Windows machine that has the client. tree-sitter parses
// C# with the same grammar VS Code uses: it will not catch a type error, but it
// does catch the class of mistake that otherwise costs a build cycle (an
// unbalanced brace, a stray parenthesis, a half-edited statement).
//
//   npm i web-tree-sitter@0.22.6 tree-sitter-wasms
//   node Tools/CsParseCheck/check.cjs $(find . -name '*.cs' -not -path '*/obj/*')
//
// Known false positive: `from` used as an argument name
// (Bots/DungeonBuddy/Avoidance/Helpers.cs) - the grammar treats the contextual
// keyword as part of a query expression. C# itself compiles it.
const Parser = require('web-tree-sitter');
const fs = require('fs');
const path = require('path');

(async () => {
  await Parser.init();
  const wasm = path.join(__dirname, 'node_modules/tree-sitter-wasms/out/tree-sitter-c_sharp.wasm');
  const lang = await Parser.Language.load(wasm);
  const parser = new Parser();
  parser.setLanguage(lang);

  let bad = 0;
  for (const f of process.argv.slice(2)) {
    const tree = parser.parse(fs.readFileSync(f, 'utf8'));
    const errs = [];
    (function walk(n) {
      if (n.type === 'ERROR' || n.isMissing)
        errs.push(`${n.type}${n.isMissing ? '(missing)' : ''} at ${n.startPosition.row + 1}:${n.startPosition.column + 1}`);
      for (const c of n.children) walk(c);
    })(tree.rootNode);
    if (errs.length) {
      bad++;
      console.log(`${f}: ${errs.length} problem(s)`);
      errs.slice(0, 10).forEach(e => console.log('   ' + e));
    }
  }
  console.log(bad === 0 ? 'all files parse' : `${bad} file(s) do not parse`);
  process.exit(bad ? 1 : 0);
})();
