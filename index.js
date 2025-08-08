const path = require('path');
const express = require('express');
const app = express();
const router = express.Router();
app.use('/www', express.static(path.join(__dirname, 'www')));

const UrlRootingMiddleware = (req, res, next) => {
  console.log(`[${new Date()}] ${req.method} ${req.url}`);
  next();
}
app.use(UrlRootingMiddleware);

app.get('/', (req, res) => {
  // res.send('test');
  res.redirect('/www/index.html');
});

app.listen(3000, () => {
  console.log('Server listening on port 3000');
});