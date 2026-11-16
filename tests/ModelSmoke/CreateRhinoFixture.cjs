// Optional maintainer fixture generator; the smoke test itself does not need Node.
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '../..');
const rhinoFactory = require(path.join(root, 'assets/model-viewer/vendor/rhino/rhino3dm.min.js'));
rhinoFactory().then(rhino => {
  const mesh = new rhino.Mesh();
  const vertices = [[0,0,0],[20,0,0],[20,20,0],[0,20,0],[0,0,20],[20,0,20],[20,20,20],[0,20,20]];
  const faces = [[0,3,2,1],[4,5,6,7],[0,1,5,4],[1,2,6,5],[2,3,7,6],[3,0,4,7]];
  vertices.forEach(v => mesh.vertices().add(...v));
  faces.forEach(f => mesh.faces().addQuadFace(...f));
  const model = new rhino.File3dm();
  const attributes = new rhino.ObjectAttributes();
  attributes.name = 'Original QuickPreview cube fixture';
  model.objects().add(mesh, attributes);
  fs.mkdirSync(path.join(__dirname, 'fixtures'), {recursive:true});
  fs.writeFileSync(path.join(__dirname, 'fixtures/cube.3dm'), model.toByteArray());
});
