import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { MaterialPool } from './material-pool.js';

export class ModelRenderer {
  constructor(canvas, onSelect) {
    this.canvas = canvas;
    this.onSelect = onSelect;
    this.groups = new Map();
    this.materials = new MaterialPool();
    this.waitingForDraw = [];
    this.origin = new THREE.Vector3();
    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color('#0b131b');
    this.camera = new THREE.PerspectiveCamera(45, 1, 0.01, 1e7);
    this.camera.up.set(0, 0, 1);
    this.camera.position.set(12, -18, 12);
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    this.controls = new OrbitControls(this.camera, canvas);
    this.controls.enableDamping = false;
    this.controls.listenToKeyEvents(canvas);
    this.scene.add(new THREE.HemisphereLight(0xddefff, 0x4b5865, 2.5));
    const light = new THREE.DirectionalLight(0xffffff, 2.5);
    light.position.set(10, -10, 25);
    this.scene.add(light);
    this.warmScene = new THREE.Scene();
    this.warmScene.add(...this.scene.children.filter(child => child.isLight).map(child => child.clone()));
    this.warmTarget = new THREE.WebGLRenderTarget(1, 1, { depthBuffer: false });
    this.grid = new THREE.GridHelper(100, 100, 0x425d70, 0x1b3040);
    this.grid.rotation.x = Math.PI / 2;
    this.scene.add(this.grid);
    this.observer = new ResizeObserver(() => this.resize());
    this.observer.observe(canvas);
    this.controls.addEventListener('change', () => this.render());
    let down;
    canvas.addEventListener('pointerdown', event => { down = [event.clientX, event.clientY]; });
    canvas.addEventListener('pointerup', event => {
      if (down && Math.hypot(down[0] - event.clientX, down[1] - event.clientY) < 4 && event.button === 0) this.pick(event);
      down = null;
    });
    this.resize();
  }

  resize() {
    const width = this.canvas.clientWidth, height = this.canvas.clientHeight;
    if (!width || !height) return;
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.render();
  }

  reset(elements) {
    for (const id of [...this.groups.keys()]) this.remove(id);
    // Rebase before converting to float32 to preserve small details at survey coordinates.
    const bounds = new THREE.Box3();
    const point = new THREE.Vector3();
    for (const element of elements) for (const mesh of element.meshes)
      for (let i = 0; i < mesh.positions.length; i += 3)
        bounds.expandByPoint(point.fromArray(mesh.positions, i));
    this.origin.copy(bounds.isEmpty() ? new THREE.Vector3() : bounds.getCenter(point));
    for (const element of elements) this.replace(element);
    this.fit();
  }

  replace(element) {
    this.remove(element.id);
    const group = this.build(element, this.origin);
    this.groups.set(element.id, group);
    this.scene.add(group);
    if (this.selected === element.id) this.highlight(element.id);
    this.render();
  }

  build(element, origin) {
    const group = new THREE.Group();
    group.userData.id = element.id;
    for (const mesh of element.meshes) {
      const positions = ArrayBuffer.isView(mesh.positions) ? mesh.positions
        : new Float32Array(mesh.positions.map((value, i) => value - origin.getComponent(i % 3)));
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
      geometry.setIndex(ArrayBuffer.isView(mesh.indices) ? new THREE.BufferAttribute(mesh.indices, 1) : mesh.indices);
      if (mesh.normals) geometry.setAttribute('normal', new THREE.BufferAttribute(mesh.normals, 3));
      else geometry.computeVertexNormals();
      const pooled = this.materials.acquire(mesh);
      const part = new THREE.Mesh(geometry, pooled.material);
      part.userData.materialKey = pooled.key;
      part.userData.baseMaterial = pooled.material;
      group.add(part);
    }
    try { this.prepareGpu(group); return group; }
    catch (error) { this.disposeGroup(group); throw error; }
  }

  prepareGpu(group) {
    // Upload buffers while the revision is staged, instead of uploading the entire model on its first visible frame.
    const previous = this.renderer.getRenderTarget();
    group.traverse(child => { if (child.isMesh) child.frustumCulled = false; });
    this.warmScene.add(group);
    try { this.renderer.setRenderTarget(this.warmTarget); this.renderer.render(this.warmScene, this.camera); }
    finally {
      this.warmScene.remove(group); this.renderer.setRenderTarget(previous);
      group.traverse(child => { if (child.isMesh) child.frustumCulled = true; });
    }
  }

  swap(groups, origin) {
    const retired = [];
    for (const [id, group] of this.groups) if (groups.get(id) !== group) { this.scene.remove(group); retired.push(group); }
    for (const [id, group] of groups) if (this.groups.get(id) !== group) this.scene.add(group);
    const initial = this.groups.size === 0;
    const delta = this.origin.clone().sub(origin);
    this.camera.position.add(delta); this.controls.target.add(delta);
    this.groups = groups; this.origin.copy(origin);
    this.highlight(this.selected);
    if (initial) this.fit(); else this.render();
    return retired;
  }

  disposeGroup(group) {
    group.traverse(child => {
      child.geometry?.dispose();
      if (child.userData.baseMaterial && child.material !== child.userData.baseMaterial) child.material.dispose();
      if (child.userData.materialKey) this.materials.release(child.userData.materialKey);
    });
  }

  remove(id) {
    const group = this.groups.get(id);
    if (!group) return;
    this.disposeGroup(group);
    this.scene.remove(group);
    this.groups.delete(id);
  }

  highlight(id) {
    const previous = this.groups.get(this.selected);
    previous?.traverse(child => {
      if (child.userData.baseMaterial && child.material !== child.userData.baseMaterial) {
        child.material.dispose(); child.material = child.userData.baseMaterial;
      }
    });
    this.selected = id;
    this.groups.get(id)?.traverse(child => {
      if (child.userData.baseMaterial) { child.material = child.userData.baseMaterial.clone(); child.material.emissive.set('#176154'); }
    });
    this.render();
  }

  pick(event) {
    const rect = this.canvas.getBoundingClientRect();
    const pointer = new THREE.Vector2((event.clientX - rect.left) / rect.width * 2 - 1,
      -(event.clientY - rect.top) / rect.height * 2 + 1);
    const ray = new THREE.Raycaster();
    ray.setFromCamera(pointer, this.camera);
    const hit = ray.intersectObjects([...this.groups.values()], true)[0];
    const id = hit?.object.parent.userData.id ?? null;
    this.highlight(id);
    this.onSelect(id);
  }

  fit() {
    const box = new THREE.Box3();
    for (const group of this.groups.values()) box.expandByObject(group);
    if (box.isEmpty()) { this.render(); return; }
    const center = box.getCenter(new THREE.Vector3());
    const size = Math.max(box.getSize(new THREE.Vector3()).length(), 0.1);
    const distance = size / (2 * Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2))) * 1.3;
    this.controls.target.copy(center);
    this.camera.position.copy(center).add(new THREE.Vector3(1, -1.4, 0.9).normalize().multiplyScalar(distance));
    this.camera.near = Math.max(size / 10000, 0.001);
    this.camera.far = Math.max(size * 100, 1000);
    this.camera.updateProjectionMatrix();
    this.grid.position.z = box.min.z - 0.01;
    this.controls.update();
    this.render();
  }

  render() {
    if (this.drawPending) return;
    this.drawPending = true;
    requestAnimationFrame(() => {
      this.drawPending = false;
      const callbacks = this.waitingForDraw.splice(0);
      try {
        if (this.renderer.getContext().isContextLost()) throw new Error('WebGL perdió el contexto; recarga el visor');
        this.renderer.render(this.scene, this.camera);
        for (const callback of callbacks) callback.resolve();
      } catch (error) { for (const callback of callbacks) callback.reject(error); }
    });
  }
  afterRender() { return new Promise((resolve, reject) => { this.waitingForDraw.push({ resolve, reject }); this.render(); }); }
}
