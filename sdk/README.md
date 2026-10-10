# BYOA libraries (version 0.0.1, early draft)

Reference implementations of the first BYOA rules, in two languages that must agree exactly. They check a requester's **manifest** and decide whether one **call** may go ahead under the visitor's **grant** (`allowed`, `warn`, `ask` or `blocked`). The rules are in [`../spec/rules-0.1-draft.md`](../spec/rules-0.1-draft.md), a draft for discussion.

| Folder | What | Package |
|---|---|---|
| [`python/`](python/) | Python 3.9+, standard library only; `import bringyourownagent` | `bringyourownagent` on PyPI |
| [`js/`](js/) | Node.js 18+, ES module, zero dependencies, TypeScript types | `bringyourownagent` on npm |
| [`test-vectors/`](test-vectors/) | Shared JSON cases, input and expected result, that both libraries must pass | — |
| [`examples/`](examples/) | A manifest, a grant, calls and a state for trying the `bringyourownagent` command line tool | — |

Every public name is `bringyourownagent`: the PyPI distribution, the Python import package, the npm package and the command in both. The short name `byoa` is only the protocol version field inside documents (`"byoa": "0.1-draft"`).

The shared test vectors are the main guarantee that the two libraries agree. Every rule change goes into the spec first, then into the vectors, then into both libraries.

## Running the tests

Python, from `sdk/python/` (no install needed; the tests use the source tree):

```sh
python -m unittest discover -s tests -t tests
```

Node.js, from `sdk/js/`:

```sh
npm test        # runs: node --test
```

## Trying the command line tool

From `sdk/`, with either implementation:

```sh
python -m bringyourownagent validate examples/manifest.json   # with sdk/python/src on PYTHONPATH, or after pip install
node js/bin/bringyourownagent.js check --manifest examples/manifest.json --grant examples/grant.json \
    --call examples/call-long-clip.json --state examples/state.json
```

## Building packages

```sh
cd python && python -m build      # wheel and sdist into python/dist/
cd js && npm pack --dry-run       # lists what would be published
```

## License

Code: [Apache License 2.0](../LICENSE). The specification and other documents: [CC BY 4.0](../LICENSE-DOCS).
